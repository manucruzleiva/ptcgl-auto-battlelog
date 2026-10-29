// Draws the pictures of the quick-setup guides: docs/quick-setup/images/<lang>/<step>.png, one per
// step of docs/quick-setup/steps.json and language, from tools/quick-setup/illustrations.html.
//
//   npm install --no-save playwright && npx playwright install chromium   (once)
//   node tools/quick-setup/render.mjs [step ...]
//
// Japanese, Korean and Chinese need the Noto Sans CJK fonts installed (Debian/Ubuntu: fonts-noto-cjk).
// The cards and deck sprites load from images.pokemontcg.io and r2.limitlesstcg.net: it needs the network.
// With pngquant on PATH (pip install pngquant-cli) the pictures are then compressed about 4x, so keep it installed.
// Run it after changing the pictures, their texts or a step, and commit the images it writes.
import { chromium } from 'playwright';
import { spawnSync } from 'node:child_process';
import { readFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..');
const guide = JSON.parse(readFileSync(join(root, 'docs', 'quick-setup', 'steps.json'), 'utf8'));
const text = JSON.parse(readFileSync(join(here, 'illustration-text.json'), 'utf8'));
const only = process.argv.slice(2);
const steps = Object.keys(guide.steps).filter((s) => only.length === 0 || only.includes(s));

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 960, height: 540 }, deviceScaleFactor: 1 });
await page.goto(pathToFileURL(join(here, 'illustrations.html')).href);
const drawn = await page.evaluate(() => window.PICTURE_IDS);
const missing = steps.filter((s) => !drawn.includes(s));
if (missing.length) throw new Error('illustrations.html has no picture for: ' + missing.join(', '));

for (const lang of guide.languages) {
  if (!text[lang]) throw new Error('illustration-text.json has no "' + lang + '"');
  mkdirSync(join(root, 'docs', 'quick-setup', 'images', lang), { recursive: true });
  for (const step of steps) {
    await page.evaluate(([s, t, l]) => window.render(s, t, l), [step, text[lang], lang]);
    const file = join(root, 'docs', 'quick-setup', 'images', lang, step + '.png');
    await page.locator('#stage').screenshot({ path: file });
  }
  console.log(lang + ': ' + steps.length + ' pictures');
}
await browser.close();

const written = guide.languages.flatMap((lang) => steps.map((step) => join(root, 'docs', 'quick-setup', 'images', lang, step + '.png')));
const quant = spawnSync('pngquant', ['--quality', '80-95', '--speed', '1', '--skip-if-larger', '--force', '--ext', '.png', ...written], { stdio: 'inherit' });
if (quant.error) console.log('pngquant not found: the pictures are not compressed (pip install pngquant-cli)');
else if (quant.status !== 0 && quant.status !== 98 && quant.status !== 99) throw new Error('pngquant failed: ' + quant.status);
