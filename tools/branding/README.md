# NyKur Edge native identity

`app-icon.svg` is the editable master for the Windows application icon. It carries
the website's half-orb and vertical-edge silhouette into a dark ink tile, with
restrained cyan glass and fine filament accents. The silhouette remains legible
at small Windows tray and shortcut sizes. The drawing is original NyKur Edge
artwork; it contains no stock images, external fonts, or third-party artwork.

The generator replaces the nine assets in `src/NyKurEdge.App/Assets` at their
existing Windows manifest dimensions. Wide and splash assets retain transparent
backgrounds and center the mark. The ICO contains 16, 20, 24, 32, 40, 48, 64, 96,
128, and 256-pixel PNG images for Windows 10 and Windows 11.

To regenerate, use Node.js 20.9 or newer and run:

```powershell
cd tools/branding
npm ci
npm run generate
```

Native runtime dependencies are unaffected: `sharp` is a development-only asset
tool. Review `artifacts/branding/app-icon-sizes.png` at its actual pixel size and
`artifacts/branding/app-icon-1024.png` after changing the SVG. These local previews
are ignored by the application's existing `artifacts/` rule.

When the toolchain already supplies `sharp`, `NYKUR_SHARP_MODULE` may point to its
module entry path before running `node generate.mjs`. This changes tool loading
only; asset generation always uses the checked-in SVG.
