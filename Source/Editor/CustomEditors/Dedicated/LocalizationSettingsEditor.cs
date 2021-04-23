// Copyright (c) 2012-2021 Wojciech Figat. All rights reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FlaxEditor.Content.Settings;
using FlaxEditor.CustomEditors.Editors;
using FlaxEditor.Scripting;
using FlaxEngine;
using FlaxEngine.GUI;
using FlaxEngine.Utilities;
using Object = FlaxEngine.Object;

namespace FlaxEditor.CustomEditors.Dedicated
{
    [CustomEditor(typeof(LocalizationSettings))]
    sealed class LocalizationSettingsEditor : GenericEditor
    {
        private CultureInfo _theMostTranslatedCulture;
        private int _theMostTranslatedCultureCount;

        /// <inheritdoc />
        public override void Initialize(LayoutElementsContainer layout)
        {
            var settings = (LocalizationSettings)Values[0];
            var tablesLength = settings.LocalizedStringTables?.Length ?? 0;
            var tables = new List<LocalizedStringTable>(tablesLength);
            for (int i = 0; i < tablesLength; i++)
            {
                var table = settings.LocalizedStringTables[i];
                if (table && !table.WaitForLoaded())
                    tables.Add(table);
            }
            var locales = tables.GroupBy(x => x.Locale);
            var tableEntries = new Dictionary<LocalizedStringTable, Dictionary<string, string[]>>();
            var allKeys = new HashSet<string>();
            foreach (var e in locales)
            {
                foreach (var table in e)
                {
                    var entries = table.Entries;
                    tableEntries[table] = entries;
                    allKeys.AddRange(entries.Keys);
                }
            }

            {
                var group = layout.Group("Preview");

                // Current language and culture preview management
                group.Object("Current Language", new CustomValueContainer(new ScriptType(typeof(CultureInfo)), Localization.CurrentLanguage, (instance, index) => Localization.CurrentLanguage, (instance, index, value) => Localization.CurrentLanguage = value as CultureInfo), null, "Current UI display language for the game preview.");
                group.Object("Current Culture", new CustomValueContainer(new ScriptType(typeof(CultureInfo)), Localization.CurrentCulture, (instance, index) => Localization.CurrentCulture, (instance, index, value) => Localization.CurrentCulture = value as CultureInfo), null, "Current values formatting culture for the game preview.");
            }

            {
                var group = layout.Group("Locales");

                // Show all existing locales
                _theMostTranslatedCulture = null;
                _theMostTranslatedCultureCount = -1;
                foreach (var e in locales)
                {
                    var culture = new CultureInfo(e.Key);
                    var prop = group.AddPropertyItem(CultureInfoEditor.GetName(culture), culture.NativeName);
                    int count = e.Sum(x => tableEntries[x].Count);
                    int validCount = e.Sum(x => tableEntries[x].Values.Count(y => y != null && y.Length != 0 && !string.IsNullOrEmpty(y[0])));
                    if (count > _theMostTranslatedCultureCount)
                    {
                        _theMostTranslatedCulture = culture;
                        _theMostTranslatedCultureCount = count;
                    }
                    prop.Label(string.Format("Progress: {0}% ({1}/{2})", (int)(((float)validCount / allKeys.Count * 100.0f)), validCount, allKeys.Count));
                    prop.Label("Tables:");
                    foreach (var table in e)
                    {
                        var namePath = table.Path;
                        if (namePath.StartsWith(Globals.ProjectFolder))
                            namePath = namePath.Substring(Globals.ProjectFolder.Length + 1);
                        var tableLabel = prop.ClickableLabel(namePath).CustomControl;
                        tableLabel.TextColorHighlighted = Color.Wheat;
                        tableLabel.DoubleClick += delegate { Editor.Instance.Windows.ContentWin.Select(table); };
                    }
                    group.Space(10);
                }

                // Update add button
                var update = group.Button("Update").Button;
                update.Height = 16.0f;
                update.Clicked += RebuildLayout;

                // New locale add button
                var addLocale = group.Button("Add Locale...").Button;
                addLocale.Height = 16.0f;
                addLocale.ButtonClicked += delegate(Button button)
                {
                    var menu = CultureInfoEditor.CreatePicker(null, culture =>
                    {
                        var displayName = CultureInfoEditor.GetName(culture);
                        if (locales.Any(x => x.Key == culture.Name))
                        {
                            MessageBox.Show($"Culture '{displayName}' is already added.");
                            return;
                        }
                        Editor.Log($"Adding culture '{displayName}' to localization settings");
                        var newTables = settings.LocalizedStringTables.ToList();
                        if (_theMostTranslatedCulture != null)
                        {
                            // Duplicate localization for culture with the highest amount of keys
                            var g = locales.First(x => x.Key == _theMostTranslatedCulture.Name);
                            foreach (var e in g)
                            {
                                var path = e.Path;
                                var filename = Path.GetFileNameWithoutExtension(path);
                                if (filename.EndsWith(_theMostTranslatedCulture.Name))
                                    filename = filename.Substring(0, filename.Length - _theMostTranslatedCulture.Name.Length);
                                path = Path.Combine(Path.GetDirectoryName(path), filename + culture.Name + ".json");
                                var table = FlaxEngine.Content.CreateVirtualAsset<LocalizedStringTable>();
                                table.Locale = culture.Name;
                                var entries = new Dictionary<string, string[]>();
                                foreach (var ee in tableEntries[e])
                                {
                                    var vv = (string[])ee.Value.Clone();
                                    for (var i = 0; i < vv.Length; i++)
                                        vv[i] = string.Empty;
                                    entries.Add(ee.Key, vv);
                                }
                                table.Entries = entries;
                                if (!table.Save(path))
                                {
                                    Object.Destroy(table);
                                    newTables.Add(FlaxEngine.Content.LoadAsync<LocalizedStringTable>(path));
                                }
                            }
                        }
                        else
                        {
                            // No localization so initialize with empty table
                            var path = Path.Combine(Path.Combine(Path.GetDirectoryName(GameSettings.Load().Localization.Path), "Localization", culture.Name + ".json"));
                            var table = FlaxEngine.Content.CreateVirtualAsset<LocalizedStringTable>();
                            table.Locale = culture.Name;
                            if (!table.Save(path))
                            {
                                Object.Destroy(table);
                                newTables.Add(FlaxEngine.Content.LoadAsync<LocalizedStringTable>(path));
                            }
                        }
                        settings.LocalizedStringTables = newTables.ToArray();
                        Presenter.OnModified();
                        RebuildLayout();
                    });
                    menu.Show(button, new Vector2(0, button.Height));
                };

                // Export button
                var exportLocalization = group.Button("Export...").Button;
                exportLocalization.Height = 16.0f;
                exportLocalization.Clicked += delegate
                {
                    if (FileSystem.ShowSaveFileDialog(null, null, "*.pot", false, "Export localization for translation to .pot file", out var filenames))
                        return;
                    if (!filenames[0].EndsWith(".pot"))
                        filenames[0] += ".pot";
                    var nplurals = 1;
                    foreach (var e in tableEntries)
                    {
                        foreach (var value in e.Value.Values)
                        {
                            if (value != null && value.Length > nplurals)
                                nplurals = value.Length;
                        }
                    }
                    using (var writer = new StreamWriter(filenames[0], false, Encoding.UTF8))
                    {
                        writer.WriteLine("msgid \"\"");
                        writer.WriteLine("msgstr \"\"");
                        writer.WriteLine("\"Language: English\\n\"");
                        writer.WriteLine("\"MIME-Version: 1.0\\n\"");
                        writer.WriteLine("\"Content-Type: text/plain; charset=UTF-8\\n\"");
                        writer.WriteLine("\"Content-Transfer-Encoding: 8bit\\n\"");
                        writer.WriteLine($"\"Plural-Forms: nplurals={nplurals}; plural=(n != 1);\\n\"");
                        writer.WriteLine("\"X-Generator: FlaxEngine\\n\"");
                        var written = new HashSet<string>();
                        foreach (var e in tableEntries)
                        {
                            foreach (var pair in e.Value)
                            {
                                if (written.Contains(pair.Key))
                                    continue;
                                written.Add(pair.Key);

                                writer.WriteLine("");
                                writer.WriteLine($"msgid \"{pair.Key}\"");
                                if (pair.Value == null || pair.Value.Length < 2)
                                {
                                    writer.WriteLine("msgstr \"\"");
                                }
                                else
                                {
                                    writer.WriteLine("msgid_plural \"\"");
                                    for (int i = 0; i < pair.Value.Length; i++)
                                        writer.WriteLine($"msgstr[{i}] \"\"");
                                }
                            }
                            if (written.Count == allKeys.Count)
                                break;
                        }
                    }
                };

                // Find localized strings in code button
                var findStringsCode = group.Button("Find localized strings in code").Button;
                findStringsCode.Height = 16.0f;
                findStringsCode.Clicked += delegate
                {
                    var newKeys = new Dictionary<string, string>();

                    // C#
                    var files = Directory.GetFiles(Globals.ProjectSourceFolder, "*.cs", SearchOption.AllDirectories);
                    var filesCount = files.Length;
                    foreach (var file in files)
                        FindNewKeysCSharp(file, newKeys);

                    // C++
                    files = Directory.GetFiles(Globals.ProjectSourceFolder, "*.cpp", SearchOption.AllDirectories);
                    filesCount += files.Length;
                    foreach (var file in files)
                        FindNewKeysCpp(file, newKeys);
                    files = Directory.GetFiles(Globals.ProjectSourceFolder, "*.h", SearchOption.AllDirectories);
                    filesCount += files.Length;
                    foreach (var file in files)
                        FindNewKeysCpp(file, newKeys);

                    Editor.Log($"Found {newKeys.Count} new localized strings in {filesCount} files");
                    if (newKeys.Count == 0)
                        return;
                    foreach (var e in newKeys)
                        Editor.Log(e.Key + (e.Value != null ? " = " + e.Value : string.Empty));
                    foreach (var locale in locales)
                    {
                        var table = locale.First();
                        var entries = tableEntries[table];
                        if (table.Locale == "en")
                        {
                            foreach (var e in newKeys)
                                entries[e.Key] = new[] { e.Value };
                        }
                        else
                        {
                            foreach (var e in newKeys)
                                entries[e.Key] = new[] { string.Empty };
                        }
                        table.Entries = entries;
                        table.Save();
                    }
                };
            }

            {
                // Raw asset data editing
                var group = layout.Group("Data");
                base.Initialize(group);
            }
        }

        private static void FindNewKeysCSharp(string file, Dictionary<string, string> newKeys)
        {
            var startToken = "Localization.GetString";
            var textToken = "\"";
            FindNewKeys(file, newKeys, startToken, textToken);
        }

        private static void FindNewKeysCpp(string file, Dictionary<string, string> newKeys)
        {
            var startToken = "Localization::GetString";
            var textToken = "TEXT(\"";
            FindNewKeys(file, newKeys, startToken, textToken);
        }

        private static void FindNewKeys(string file, Dictionary<string, string> newKeys, string startToken, string textToken)
        {
            var contents = File.ReadAllText(file);
            var idx = contents.IndexOf(startToken);
            while (idx != -1)
            {
                idx += startToken.Length + 1;
                int braces = 1;
                int start = idx;
                while (idx < contents.Length && braces != 0)
                {
                    if (contents[idx] == '(')
                        braces++;
                    if (contents[idx] == ')')
                        braces--;
                    idx++;
                }
                if (idx == contents.Length)
                    break;
                var inside = contents.Substring(start, idx - start - 1);
                var textStart = inside.IndexOf(textToken);
                if (textStart != -1)
                {
                    textStart += textToken.Length;
                    var textEnd = textStart;
                    while (textEnd < inside.Length && inside[textEnd] != '\"')
                    {
                        if (inside[textEnd] == '\\')
                            textEnd++;
                        textEnd++;
                    }
                    var id = inside.Substring(textStart, textEnd - textStart);
                    textStart = inside.Length > textEnd + 2 ? inside.IndexOf(textToken, textEnd + 2) : -1;
                    string value = null;
                    if (textStart != -1)
                    {
                        textStart += textToken.Length;
                        textEnd = textStart;
                        while (textEnd < inside.Length && inside[textEnd] != '\"')
                        {
                            if (inside[textEnd] == '\\')
                                textEnd++;
                            textEnd++;
                        }
                        value = inside.Substring(textStart, textEnd - textStart);
                    }

                    newKeys[id] = value;
                }

                idx = contents.IndexOf(startToken, idx);
            }
        }
    }
}
