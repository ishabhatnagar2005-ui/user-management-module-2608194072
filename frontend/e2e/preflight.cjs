/** Capture representative rendered semantics and styles without repeating journey specs. */
const { chromium } = require('@playwright/test');
const fs = require('node:fs');
const path = require('node:path');
async function main() {
  const base = process.env.E2E_FRONTEND_URL || 'http://127.0.0.1:4200';
  const browser = await chromium.launch({ args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  const page = await browser.newPage();
  const output = path.resolve(__dirname, '../../preflight-evidence');
  fs.mkdirSync(output, { recursive: true });
  const sections = [];
  /** Persist text evidence plus screenshots for the human reviewer. */
  async function capture(name) {
    const session = await page.context().newCDPSession(page);
    const tree = await session.send('Accessibility.getFullAXTree');
    const styles = await page.evaluate(() => ({ font: getComputedStyle(document.body).fontFamily,
      color: getComputedStyle(document.body).color, background: getComputedStyle(document.documentElement).backgroundColor,
      width: innerWidth, documentWidth: document.documentElement.scrollWidth,
      headings: [...document.querySelectorAll('h1,h2')].map(x => x.textContent),
      controls: [...document.querySelectorAll('input,button,select')].map(x => ({ tag: x.tagName, label: x.getAttribute('aria-label'), height: getComputedStyle(x).height })) }));
    sections.push(name + '\n' + JSON.stringify(styles) + '\n' + JSON.stringify(tree) + '\n' + await page.content());
    await page.screenshot({ path: path.join(output, name + '.png'), fullPage: true });
    await session.detach();
  }
  await page.goto(base); await capture('home-desktop');
  await page.getByRole('link', { name: 'Sign in', exact: true }).first().click();
  await capture('login-desktop');
  const config = JSON.parse(fs.readFileSync(path.resolve(__dirname, '../../backend/appsettings.Development.json'), 'utf8'));
  await page.getByLabel('Username', { exact: true }).fill(config.Seed.AdminUsername);
  await page.getByLabel('Password', { exact: true }).fill(config.Seed.AdminPassword);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('button', { name: 'Sign out' }).waitFor();
  await page.getByRole('navigation').getByRole('link', { name: 'Dashboard', exact: true }).click();
  await page.getByRole('heading', { name: 'Dashboard', exact: true }).waitFor();
  await page.waitForTimeout(500); await capture('dashboard-desktop');
  await page.setViewportSize({ width: 393, height: 851 });
  await page.getByRole('navigation').getByRole('link', { name: 'Directory', exact: true }).click();
  await page.getByRole('heading', { name: 'Account directory', exact: true }).waitFor();
  await page.waitForTimeout(500); await capture('directory-mobile');
  fs.writeFileSync(path.join(output, 'review.txt'), sections.join('\n\n'));
  await browser.close();
}
main().catch(error => { console.error(error.message); process.exitCode = 1; });
