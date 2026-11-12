/** Browse real synthetic directory records through UI navigation, never route mocks. */
import { test, expect, signIn, credentials } from './fixtures';
import { randomUUID } from 'node:crypto';
import { DirectoryPage, UserView } from '../src/app/core/api.models';
/** Exercise shared URL filters, paging, inert controls and persisted safe detail on desktop/mobile. */
test('Admin organizes the real directory and preserves query state through history', async ({ page, request }) => {
  const marker = `dir.${randomUUID().replace(/-/g, '')}`;
  const admin = credentials('Admin');
  // Setup only uses real API; all directory feature interactions below use the browser UI.
  const login = await request.post('/api/login', { data: admin }); expect(login.status()).toBe(200);
  const token = (await login.json()).accessToken as string;
  const headers = { Authorization: `Bearer ${token}` };
  const accounts: UserView[] = [];
  for (const suffix of ['alpha', 'beta']) {
    const response = await request.post('/api/users', { headers, data: { username: `${marker}.${suffix}`, password: `Synthetic-${randomUUID()}`, city: `Synthetic ${suffix}`, country: marker, education: marker, pan_no: `SYNTHETIC-${suffix}` } });
    expect(response.status()).toBe(201); accounts.push(await response.json() as UserView);
  }
  const writes: string[] = [];
  page.on('request', r => { if (['POST', 'PUT', 'DELETE'].includes(r.method()) && new URL(r.url()).pathname.startsWith('/api/users')) writes.push(r.method()); });
  /** Await one real collection response and assert its approved envelope. */
  const collection = () => page.waitForResponse(r => new URL(r.url()).pathname === '/api/users' && r.request().method() === 'GET');
  /** Read a live response rather than inferring correctness from a heading. */
  const assertPage = async (response: Awaited<ReturnType<typeof collection>>): Promise<DirectoryPage> => {
    expect(response.status()).toBe(200); const body = await response.json() as DirectoryPage;
    expect(Object.keys(body).sort()).toEqual(['items', 'limit', 'skip']);
    for (const item of body.items) expect(Object.keys(item).sort()).toEqual(['address', 'city', 'country', 'created_at', 'education', 'id', 'is_active', 'updated_at', 'username']);
    return body;
  };
  await page.goto('/');
  await page.getByRole('main').getByRole('link', { name: 'Sign in', exact: true }).click(); await signIn(page, 'Admin');
  const initial = collection(); await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Directory', exact: true }).click();
  await assertPage(await initial);
  await page.getByRole('main').getByRole('link', { name: 'New account', exact: true }).click();
  await expect(page).toHaveURL('/users/new');
  await expect(page.getByRole('main').getByRole('heading', { name: 'New account', exact: true })).toBeVisible();
  await expect(page.getByRole('main').locator('form')).toBeVisible();
  await expect(page.getByLabel('Username', { exact: true })).toBeEditable();
  await expect(page.getByLabel('Password', { exact: true })).toBeEditable();
  await expect(page.getByRole('button', { name: 'Create account', exact: true })).toBeEnabled();
  await page.getByRole('main').getByRole('link', { name: 'Cancel and return home', exact: true }).click();
  await expect(page).toHaveURL('/');
  const returned = collection();
  await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Directory', exact: true }).click();
  await assertPage(await returned);
  await expect(page).toHaveURL('/users');
  // Slow the actual network without replacing any response so transient loading is observable.
  const session = await page.context().newCDPSession(page);
  await session.send('Network.enable');
  await session.send('Network.emulateNetworkConditions', { offline: false, latency: 250, downloadThroughput: -1, uploadThroughput: -1 });
  await page.getByLabel('Search', { exact: true }).fill(marker);
  await page.getByLabel('Country', { exact: true }).fill(marker); await page.getByLabel('Education', { exact: true }).fill(marker);
  await page.getByLabel('Status', { exact: true }).selectOption('active'); await page.getByLabel('Sort by', { exact: true }).selectOption('username');
  await page.getByLabel('Sort direction', { exact: true }).selectOption('desc'); await page.getByLabel('Page size', { exact: true }).fill('1');
  const filtered = collection(); await page.getByRole('button', { name: 'Apply query', exact: true }).click();
  await expect(page.getByText('Loading directory…', { exact: true })).toBeVisible();
  const first = await assertPage(await filtered); expect(first.items.map(x => x.username)).toEqual([`${marker}.beta`]); expect(first.limit).toBe(1); expect(first.skip).toBe(0);
  await expect(page.getByRole('table')).toContainText(first.items[0].city!); await expect(page).toHaveURL(/sortDirection=desc/);
  await page.getByRole('checkbox', { name: `Select ${marker}.beta`, exact: true }).check();
  await expect(page.getByText('1 rows selected', { exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: 'Bulk actions', exact: true })).toBeDisabled(); await expect(page.getByRole('button', { name: 'Export', exact: true })).toBeDisabled();
  await expect(page.getByText('CSV/Excel export is unavailable pending data-handling approval.', { exact: true })).toBeVisible();
  const secondResponse = collection(); await page.getByRole('button', { name: 'Next page', exact: true }).click();
  const second = await assertPage(await secondResponse); expect(second.skip).toBe(1); expect(second.items[0].username).toBe(`${marker}.alpha`);
  const savedUrl = page.url();
  await page.screenshot({ path: test.info().outputPath('directory-populated.png'), fullPage: true });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  const detailResponse = page.waitForResponse(r => new URL(r.url()).pathname === `/api/users/${second.items[0].id}`);
  await page.getByRole('link', { name: `${marker}.alpha`, exact: true }).click(); expect((await detailResponse).status()).toBe(200);
  await expect(page.getByRole('main')).toContainText('Synthetic alpha');
  const back = collection(); await page.goBack(); expect((await assertPage(await back)).items).toEqual(second.items); await expect(page).toHaveURL(savedUrl);
  const earlier = collection(); await page.goBack(); expect((await assertPage(await earlier)).skip).toBe(0);
  const forward = collection(); await page.goForward(); expect((await assertPage(await forward)).skip).toBe(1);
  await expect(page.getByLabel('Search', { exact: true })).toHaveValue(marker); await expect(page.getByLabel('Page size', { exact: true })).toHaveValue('1');
  await page.getByLabel('Search', { exact: true }).fill(`${marker}.absent`);
  const empty = collection(); await page.getByRole('button', { name: 'Apply query', exact: true }).click(); expect((await assertPage(await empty)).items).toEqual([]);
  await expect(page.getByText('No accounts match this query or page.', { exact: true })).toBeVisible(); await expect(page.getByRole('table')).toHaveCount(0);
  expect(writes).toEqual([]);
  for (const account of accounts) { const persisted = await request.get(`/api/users/${account.id}`, { headers }); expect(persisted.status()).toBe(200); expect(await persisted.json()).toEqual(account); }
  await page.screenshot({ path: test.info().outputPath('directory-empty.png'), fullPage: true });
  // A hard refresh follows the existing memory-only auth contract and requires sign-in again.
  await page.reload(); await expect(page).toHaveURL('/login');
  await expect(page.getByRole('heading', { name: 'Sign in', exact: true })).toBeVisible();
  await session.detach();
});
