# AnchorChain startup debugging

Read this reference only when the preloader reports
`Could not find AnchorChain chainloader`.

1. Run `pnpm launch` so Steam initializes Workshop content before Sea Power.
2. Confirm Workshop item `3380210757` is subscribed and installed.
3. Confirm its directory contains `AnchorChain.dll` under
   `steamapps\workshop\content\1286220\3380210757`.
4. Inspect `Player.log` for the Workshop item and nearby AnchorChain errors.

If the item or DLL is absent, restore the Workshop subscription before
debugging Crimean Tom.
