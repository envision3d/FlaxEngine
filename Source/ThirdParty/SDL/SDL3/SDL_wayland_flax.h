/*
  FLAX PATCH: Accessor for SDL's internal Wayland data device.
  The Wayland protocol allows a client to have only one wl_data_device per
  seat: requesting a new one makes the compositor destroy the previous device.
  Flax's drag & drop runs on SDL's own device, so it must not request its own.
*/

#ifndef SDL_wayland_flax_h_
#define SDL_wayland_flax_h_

#ifdef __cplusplus
extern "C" {
#endif

/**
 *  \brief Return the raw wl_data_device pointer of the first seat (owned by SDL).
 *
 *  \returns The wl_data_device* of the first seat with a data device, or NULL.
 */
extern void* SDL_Wayland_GetDataDevice(void);

#ifdef __cplusplus
}
#endif

#endif /* SDL_wayland_flax_h_ */
