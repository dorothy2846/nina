# NINA-Pi Known Issues & Gotchas

## [2026-03-11] Known Issues / Gotchas
- NINA.WPF.Base contains Mediator implementations → needs headless reimplementation
- 41 P/Invoke files → need Linux ARM64 compatibility check
- NOVAS/SOFA/cfitsio native libs → need ARM64 recompile
- Advanced API is a plugin → needs core integration in NINA.Headless
- net10.0-windows TFM in main NINA.csproj → NINA.Headless must use net9.0
