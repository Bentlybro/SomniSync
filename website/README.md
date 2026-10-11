# SomniSync website

This [Docusaurus](https://docusaurus.io/) site publishes the SomniSync overview and documentation at https://bentlybro.github.io/SomniSync/.

From the `website` directory:

```bash
npm ci
npm run typecheck
npm run build
npm run start
```

Edit the landing page in `src/pages/index.tsx` and its styles in `src/css/custom.css`. The canonical documentation lives in `../docs`; the matching pages under `website/docs` are checked for consistency by `tools/check_release_files.py`.

Pushes to `master` that change this site or the docs trigger `.github/workflows/deploy-docs.yml`. GitHub Pages uses the workflow deployment source and publishes the `website/build` artifact. There is no manual `gh-pages` branch to maintain.
