// Copyright (c) 2012-2022 Wojciech Figat. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Flax.Build.Graph;
using Flax.Build.Platforms;
using Flax.Deploy;
using Microsoft.Win32;

namespace Flax.Build
{
    static partial class Builder
    {
        public static event Action<TaskGraph, BuildData, NativeCpp.BuildOptions, Task, IGrouping<string, Module>> BuildDotNetAssembly;

        private static void BuildTargetDotNet(RulesAssembly rules, TaskGraph graph, Target target, Platform platform, TargetConfiguration configuration)
        {
            // Check if use custom project file
            if (!string.IsNullOrEmpty(target.CustomExternalProjectFilePath))
            {
                // Use msbuild to compile it
                var task = graph.Add<Task>();
                task.WorkingDirectory = Globals.Root;
                task.InfoMessage = "Building " + Path.GetFileName(target.CustomExternalProjectFilePath);
                task.Cost = 100;
                task.DisableCache = true;
                task.CommandPath = VCEnvironment.MSBuildPath;
                task.CommandArguments = string.Format("\"{0}\" /m /t:Build /p:Configuration=\"{1}\" /p:Platform=\"{2}\" {3} /nologo", target.CustomExternalProjectFilePath, configuration.ToString(), "AnyCPU", VCEnvironment.Verbosity);
                return;
            }

            // Warn if target has no valid modules
            if (target.Modules.Count == 0)
                Log.Warning(string.Format("Target {0} has no modules to build", target.Name));

            // Pick a project
            var project = Globals.Project;
            if (target is ProjectTarget projectTarget)
                project = projectTarget.Project;
            if (project == null)
                throw new Exception($"Cannot build target {target.Name}. The project file is missing (.flaxproj located in the folder above).");

            // Setup build environment for the target
            var targetBuildOptions = GetBuildOptions(target, platform, null, TargetArchitecture.AnyCPU, configuration, project.ProjectFolderPath);
            using (new ProfileEventScope("PreBuild"))
            {
                // Pre build
                target.PreBuild(graph, targetBuildOptions);
                PreBuild?.Invoke(graph, targetBuildOptions);

                // Ensure that target build directories exist
                if (!target.IsPreBuilt && !Directory.Exists(targetBuildOptions.IntermediateFolder))
                    Directory.CreateDirectory(targetBuildOptions.IntermediateFolder);
                if (!target.IsPreBuilt && !Directory.Exists(targetBuildOptions.OutputFolder))
                    Directory.CreateDirectory(targetBuildOptions.OutputFolder);
            }

            // Setup building common data container
            var buildData = new BuildData
            {
                Project = project,
                Graph = graph,
                Rules = rules,
                Target = target,
                TargetOptions = targetBuildOptions,
                Platform = platform,
                Architecture = TargetArchitecture.AnyCPU,
                Configuration = configuration,
            };

            // Collect all modules
            using (new ProfileEventScope("CollectModules"))
            {
                foreach (var moduleName in target.Modules)
                {
                    var module = rules.GetModule(moduleName);
                    if (module != null)
                    {
                        CollectModules(buildData, module, true);
                    }
                    else
                    {
                        Log.Warning(string.Format("Missing module {0} (or invalid name specified)", moduleName));
                    }
                }
            }

            // Build all modules from target binary modules but in order of collecting (from independent to more dependant ones)
            var sourceFiles = new List<string>();
            using (new ProfileEventScope("BuildModules"))
            {
                foreach (var module in buildData.ModulesOrderList)
                {
                    if (buildData.BinaryModules.Any(x => x.Contains(module)))
                    {
                        var moduleOptions = BuildModule(buildData, module);

                        // Get source files
                        sourceFiles.AddRange(moduleOptions.SourceFiles.Where(x => x.EndsWith(".cs")));

                        // Merge module into target environment
                        buildData.TargetOptions.LinkEnv.InputFiles.AddRange(moduleOptions.OutputFiles);
                        buildData.TargetOptions.DependencyFiles.AddRange(moduleOptions.DependencyFiles);
                        buildData.TargetOptions.OptionalDependencyFiles.AddRange(moduleOptions.OptionalDependencyFiles);
                        buildData.TargetOptions.Libraries.AddRange(moduleOptions.Libraries);
                        buildData.TargetOptions.DelayLoadLibraries.AddRange(moduleOptions.DelayLoadLibraries);
                        buildData.TargetOptions.ScriptingAPI.Add(moduleOptions.ScriptingAPI);
                        buildData.TargetOptions.ExternalModules.AddRange(moduleOptions.ExternalModules);
                    }
                }
            }

            // Build
            var outputTargetFilePath = target.GetOutputFilePath(targetBuildOptions);
            var outputPath = Path.GetDirectoryName(outputTargetFilePath);
            using (new ProfileEventScope("Build"))
            {
                // Cleanup source files
                sourceFiles.RemoveAll(x => x.EndsWith(BuildFilesPostfix));
                sourceFiles.Sort();

                // Build assembly
                BuildDotNet(graph, buildData, targetBuildOptions, target.OutputName, sourceFiles);
            }

            // Deploy files
            if (!target.IsPreBuilt)
            {
                using (new ProfileEventScope("DeployFiles"))
                {
                    foreach (var srcFile in targetBuildOptions.OptionalDependencyFiles.Where(File.Exists).Union(targetBuildOptions.DependencyFiles))
                    {
                        var dstFile = Path.Combine(outputPath, Path.GetFileName(srcFile));
                        graph.AddCopyFile(dstFile, srcFile);
                    }
                }
            }

            using (new ProfileEventScope("PostBuild"))
            {
                // Post build
                PostBuild?.Invoke(graph, targetBuildOptions);
                target.PostBuild(graph, targetBuildOptions);
            }
        }

        private static void BuildDotNet(TaskGraph graph, BuildData buildData, NativeCpp.BuildOptions buildOptions, string name, List<string> sourceFiles, HashSet<string> fileReferences = null, IGrouping<string, Module> binaryModule = null)
        {
#if USE_NETCORE
            static Version ParseVersion(string version)
            {
                // Give precedence to final releases over release candidate / beta releases
                int rev = 9999;
                if (version.Contains("-")) // e.g. 7.0.0-rc.2.22472.3
                {
                    version = version.Substring(0, version.IndexOf("-"));
                    rev = 0;
                }
                Version ver = new Version(version);
                return new Version(ver.Major, ver.Minor, ver.Build, rev);
            }
#endif
            // Setup build options
            var buildPlatform = Platform.BuildTargetPlatform;
            var outputPath = Path.GetDirectoryName(buildData.Target.GetOutputFilePath(buildOptions));
            var outputFile = Path.Combine(outputPath, name + ".dll");
            var outputDocFile = Path.Combine(outputPath, name + ".xml");
            string monoRoot, monoPath, cscPath, referenceAssemblies, referenceAnalyzers;
            switch (buildPlatform)
            {
            case TargetPlatform.Windows:
            {
                monoRoot = Path.Combine(Globals.EngineRoot, "Source", "Platforms", "Editor", "Windows", "Mono");

                // Prefer installed Roslyn C# compiler over Mono one
                monoPath = null;
                cscPath = Path.Combine(Path.GetDirectoryName(VCEnvironment.MSBuildPath), "Roslyn", "csc.exe");

#if USE_NETCORE
                // dotnet
                if (WindowsPlatformBase.TryReadDirRegistryKey(@"HKEY_LOCAL_MACHINE\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost", "Path", out string dotnetPath))
                {
#pragma warning disable CA1416
                    string arch = "x64";
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using RegistryKey sdkVersionsKey = baseKey.OpenSubKey($@"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\{arch}\sdk");
                    using RegistryKey sharedfxVersionsKey = baseKey.OpenSubKey($@"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\{arch}\sharedfx\Microsoft.NETCore.App");
                    using RegistryKey hostfxrKey = baseKey.OpenSubKey($@"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\{arch}\hostfxr");

                    string dotnetSdkVersion = sdkVersionsKey.GetValueNames().OrderByDescending(x => ParseVersion(x)).FirstOrDefault();
                    string dotnetSharedfxVersion = sharedfxVersionsKey.GetValueNames().OrderByDescending(x => ParseVersion(x)).FirstOrDefault();
#pragma warning restore CA1416
                    cscPath = @$"{dotnetPath}sdk\{dotnetSdkVersion}\Roslyn\bincore\csc.dll";
                    referenceAssemblies = @$"{dotnetPath}shared\Microsoft.NETCore.App\{dotnetSharedfxVersion}\";
                    referenceAnalyzers = @$"{dotnetPath}packs\Microsoft.NETCore.App.Ref\{dotnetSharedfxVersion}\analyzers\dotnet\cs\";
                }
                else //if (!File.Exists(cscPath))
#endif
                {
                    // Fallback to Mono binaries
                    monoPath = Path.Combine(monoRoot, "bin", "mono.exe");
                    cscPath = Path.Combine(monoRoot, "lib", "mono", "4.5", "csc.exe");
                    referenceAssemblies = Path.Combine(monoRoot, "lib", "mono", "4.5-api");
                    referenceAnalyzers = "";
                }
                break;
            }
            case TargetPlatform.Linux:
            {
#if USE_NETCORE
                // TODO: Support /etc/dotnet/install_location
                string dotnetPath = "/usr/share/dotnet/";
                string arch = "x64";
                string os = $"linux-{arch}";
                monoPath = null;

                string[] sharedfxVersions = Directory.GetDirectories($"{dotnetPath}shared/Microsoft.NETCore.App/").Select(x => Path.GetFileName(x)).ToArray();
                string dotnetSharedfxVersion = sharedfxVersions.OrderByDescending(x => ParseVersion(x)).FirstOrDefault();

                string[] sdkVersions = Directory.GetDirectories($"{dotnetPath}sdk/").Select(x => Path.GetFileName(x)).ToArray();
                string dotnetSdkVersion = sdkVersions.OrderByDescending(x => ParseVersion(x)).FirstOrDefault();

                int majorVersion = int.Parse(dotnetSdkVersion.Substring(0, dotnetSdkVersion.IndexOf(".")));
                if (majorVersion >= 7)
                {
                    cscPath = @$"{dotnetPath}sdk/{dotnetSdkVersion}/Roslyn/bincore/csc.dll";
                    referenceAssemblies = @$"{dotnetPath}shared/Microsoft.NETCore.App/{dotnetSharedfxVersion}/";
                    referenceAnalyzers = @$"{dotnetPath}packs/Microsoft.NETCore.App.Ref/{dotnetSharedfxVersion}/analyzers/dotnet/cs/";
                }
                else
#endif
                {
                    monoRoot = Path.Combine(Globals.EngineRoot, "Source", "Platforms", "Editor", "Linux", "Mono");
                    monoPath = Path.Combine(monoRoot, "bin", "mono");
                    cscPath = Path.Combine(monoRoot, "lib", "mono", "4.5", "csc.exe");
                    referenceAssemblies = Path.Combine(monoRoot, "lib", "mono", "4.5-api");
                    referenceAnalyzers = "";
                }
                break;
            }
            case TargetPlatform.Mac:
                monoRoot = Path.Combine(Globals.EngineRoot, "Source", "Platforms", "Editor", "Mac", "Mono");
                monoPath = Path.Combine(monoRoot, "bin", "mono");
                cscPath = Path.Combine(monoRoot, "lib", "mono", "4.5", "csc.exe");
                referenceAssemblies = Path.Combine(monoRoot, "lib", "mono", "4.5-api");
                referenceAnalyzers = "";
                break;
            default: throw new InvalidPlatformException(buildPlatform);
            }

            if (fileReferences == null)
                fileReferences = buildOptions.ScriptingAPI.FileReferences;
            else
                fileReferences.AddRange(buildOptions.ScriptingAPI.FileReferences);

            // Setup C# compiler arguments
            var args = new List<string>();
            args.Clear();
            args.Add("/nologo");
            args.Add("/target:library");
            args.Add("/platform:AnyCPU");
            args.Add("/debug+");
            args.Add("/debug:portable");
            args.Add("/errorreport:prompt");
            args.Add("/preferreduilang:en-US");
            args.Add("/highentropyva+");
            args.Add("/deterministic");
            args.Add("/nostdlib+");
            args.Add("/errorendlocation");
            args.Add("/utf8output");
            args.Add("/warn:4");
            args.Add("/unsafe");
            args.Add("/fullpaths");
            args.Add("/filealign:512");
#if USE_NETCORE
            args.Add("/langversion:11.0");
            args.Add("-nowarn:8632"); // Nullable
#else
            args.Add("/langversion:7.3");
#endif
            if (buildOptions.ScriptingAPI.IgnoreMissingDocumentationWarnings)
                args.Add("-nowarn:1591");
#if USE_NETCORE
            // Optimizations prevent debugging, only enable in release builds
            args.Add(buildData.Configuration == TargetConfiguration.Release ? "/optimize+" : "/optimize-");
#else
            args.Add(buildData.Configuration == TargetConfiguration.Debug ? "/optimize-" : "/optimize+");
#endif
            args.Add(string.Format("/out:\"{0}\"", outputFile));
            args.Add(string.Format("/doc:\"{0}\"", outputDocFile));
            if (buildOptions.ScriptingAPI.Defines.Count != 0)
                args.Add("/define:" + string.Join(";", buildOptions.ScriptingAPI.Defines));
            if (buildData.Configuration == TargetConfiguration.Debug)
                args.Add("/define:DEBUG");
            args.Add(string.Format("/reference:\"{0}mscorlib.dll\"", referenceAssemblies));
            foreach (var reference in buildOptions.ScriptingAPI.SystemReferences)
                args.Add(string.Format("/reference:\"{0}{1}.dll\"", referenceAssemblies, reference));
            foreach (var reference in fileReferences)
                args.Add(string.Format("/reference:\"{0}\"", reference));
#if USE_NETCORE
            foreach (var analyzer in buildOptions.ScriptingAPI.SystemAnalyzers)
                args.Add(string.Format("/analyzer:\"{0}{1}.dll\"", referenceAnalyzers, analyzer));
#endif
            foreach (var sourceFile in sourceFiles)
                args.Add("\"" + sourceFile + "\"");

            // Generate response file with source files paths and compilation arguments
            string responseFile = Path.Combine(buildOptions.IntermediateFolder, name + ".response");
            Utilities.WriteFileIfChanged(responseFile, string.Join(Environment.NewLine, args));

            // Create C# compilation task
            var task = graph.Add<Task>();
            task.PrerequisiteFiles.Add(responseFile);
            task.PrerequisiteFiles.AddRange(sourceFiles);
            task.PrerequisiteFiles.AddRange(fileReferences);
            task.ProducedFiles.Add(outputFile);
            task.WorkingDirectory = buildData.TargetOptions.WorkingDirectory;
            task.InfoMessage = "Compiling " + outputFile;
            task.Cost = task.PrerequisiteFiles.Count;

            if (monoPath != null)
            {
                task.CommandPath = monoPath;
                task.CommandArguments = $"\"{cscPath}\" /noconfig @\"{responseFile}\"";
            }
            else
            {
                // The "/shared" flag enables the compiler server support:
                // https://github.com/dotnet/roslyn/blob/main/docs/compilers/Compiler%20Server.md

#if USE_NETCORE
                task.CommandPath = "dotnet";
                task.CommandArguments = $"exec \"{cscPath}\" /noconfig /shared @\"{responseFile}\"";
#else
                task.CommandPath = cscPath;
                task.CommandArguments = $"/noconfig /shared @\"{responseFile}\"";
#endif
            }

            BuildDotNetAssembly?.Invoke(graph, buildData, buildOptions, task, binaryModule);

            // Copy referenced assemblies
            foreach (var srcFile in buildOptions.ScriptingAPI.FileReferences)
            {
                var dstFile = Path.Combine(outputPath, Path.GetFileName(srcFile));
                if (dstFile == srcFile || graph.HasCopyTask(dstFile, srcFile))
                    continue;
                graph.AddCopyFile(dstFile, srcFile);

                var srcPdb = Path.ChangeExtension(srcFile, "pdb");
                if (File.Exists(srcPdb))
                    graph.AddCopyFile(Path.ChangeExtension(dstFile, "pdb"), srcPdb);

                var srcXml = Path.ChangeExtension(srcFile, "xml");
                if (File.Exists(srcXml))
                    graph.AddCopyFile(Path.ChangeExtension(dstFile, "xml"), srcXml);
            }
        }

        private static void BuildTargetBindings(TaskGraph graph, BuildData buildData)
        {
            var sourceFiles = new List<string>();
            var fileReferences = new HashSet<string>();
            var buildOptions = buildData.TargetOptions;
            var outputPath = Path.GetDirectoryName(buildData.Target.GetOutputFilePath(buildOptions));
            foreach (var binaryModule in buildData.BinaryModules)
            {
                if (binaryModule.All(x => !x.BuildCSharp))
                    continue;
                var binaryModuleName = binaryModule.Key;
                using (new ProfileEventScope(binaryModuleName))
                {
                    // TODO: add support for extending this code and support generating bindings projects for other scripting languages
                    var project = GetModuleProject(binaryModule.First(), buildData);

                    // Get source files
                    sourceFiles.Clear();
                    foreach (var module in binaryModule)
                        sourceFiles.AddRange(buildData.Modules[module].SourceFiles.Where(x => x.EndsWith(".cs")));
                    sourceFiles.RemoveAll(x => x.EndsWith(BuildFilesPostfix));
                    var moduleGen = Path.Combine(project.ProjectFolderPath, "Source", binaryModuleName + ".Gen.cs");
                    if (!sourceFiles.Contains(moduleGen))
                        sourceFiles.Add(moduleGen);
                    sourceFiles.Sort();

                    // Get references
                    fileReferences.Clear();
                    foreach (var module in binaryModule)
                    {
                        if (!buildData.Modules.TryGetValue(module, out var moduleBuildOptions))
                            continue;

                        // Find references based on the modules dependencies
                        foreach (var dependencyName in moduleBuildOptions.PublicDependencies.Concat(moduleBuildOptions.PrivateDependencies))
                        {
                            var dependencyModule = buildData.Rules.GetModule(dependencyName);
                            if (dependencyModule != null &&
                                !string.IsNullOrEmpty(dependencyModule.BinaryModuleName) &&
                                dependencyModule.BuildCSharp &&
                                dependencyModule.BinaryModuleName != binaryModuleName &&
                                buildData.Modules.TryGetValue(dependencyModule, out var dependencyModuleOptions))
                            {
                                foreach (var x in buildData.BinaryModules)
                                {
                                    if (x.Key == null || x.Key != dependencyModule.BinaryModuleName)
                                        continue;

                                    // Reference module output binary
                                    fileReferences.Add(Path.Combine(outputPath, dependencyModule.BinaryModuleName + ".CSharp.dll"));
                                }
                                foreach (var e in buildData.ReferenceBuilds)
                                {
                                    foreach (var q in e.Value.BuildInfo.BinaryModules)
                                    {
                                        if (q.Name == dependencyModule.BinaryModuleName && !string.IsNullOrEmpty(q.ManagedPath))
                                        {
                                            // Reference binary module build build for referenced target
                                            fileReferences.Add(q.ManagedPath);
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Build assembly
                    BuildDotNet(graph, buildData, buildOptions, binaryModuleName + ".CSharp", sourceFiles, fileReferences, binaryModule);
                }
            }
        }
    }
}
