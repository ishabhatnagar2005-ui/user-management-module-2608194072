/** Exercise live Admin activity navigation and User denial without mocks or repeated entry navigation. */
import { test, expect, signIn } from './fixtures';
import { AuditPage, DashboardView } from '../src/app/core/api.models';

// Require real activity responses and match every displayed summary to the backend source snapshot.
test('Admin reads six dashboard summaries and exact safe audit records through navigation', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('main').getByRole('link', { name: 'Sign in', exact: true }).click();
  await signIn(page, 'Admin');
  const dashboardResponse = page.waitForResponse(r => new URL(r.url()).pathname === '/api/dashboard' && r.request().method() === 'GET');
  await page.getByRole('navigation').getByRole('link', { name: 'Dashboard', exact: true }).click();
  const response = await dashboardResponse; expect(response.status()).toBe(200);
  const summary = await response.json() as DashboardView;
  expect(Object.keys(summary).sort()).toEqual(['active', 'auditSummary', 'countryDistribution', 'inactive', 'recentLogins', 'total']);
  expect(summary.total).toBe(summary.active + summary.inactive);
  expect(summary.countryDistribution.reduce((count, item) => count + item.count, 0)).toBeLessThanOrEqual(summary.total);
  await expect(page).toHaveURL('/dashboard');
  await expect(page.getByRole('navigation').getByRole('link', { name: 'Dashboard', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(page.getByTestId('total')).toHaveText(String(summary.total));
  await expect(page.getByTestId('active')).toHaveText(String(summary.active));
  await expect(page.getByTestId('inactive')).toHaveText(String(summary.inactive));
  await expect(page.getByRole('main').getByRole('heading', { level: 2 })).toHaveCount(6);
  const countries = page.getByRole('region', { name: 'Country distribution' });
  for (const item of summary.countryDistribution) {
    expect(item.country.trim()).not.toBe(''); expect(item.count).toBeGreaterThan(0);
    await expect(countries).toContainText(`${item.country}: ${item.count}`);
  }
  if (!summary.countryDistribution.length) await expect(countries).toContainText('No populated countries are available.');
  const logins = page.getByRole('region', { name: 'Recent logins' });
  expect(summary.recentLogins.length).toBeLessThanOrEqual(10);
  for (const [index, item] of summary.recentLogins.entries()) {
    expect(item.action).toBe('login');
    if (index) expect(Date.parse(summary.recentLogins[index - 1].timestamp)).toBeGreaterThanOrEqual(Date.parse(item.timestamp));
    const row = logins.locator(`[data-audit-id="${item.id}"]`);
    await expect(row).toContainText(item.status); await expect(row.locator('time')).toHaveAttribute('datetime', item.timestamp);
    if (item.user_id) await expect(row).toContainText(item.user_id);
  }
  if (!summary.recentLogins.length) await expect(logins).toContainText('No login records are available.');
  const activity = page.getByRole('region', { name: 'Audit summary' });
  for (const item of summary.auditSummary) await expect(activity).toContainText(`${item.action}: ${item.success} success, ${item.failure} failure`);
  if (!summary.auditSummary.length) await expect(activity).toContainText('No audit activity is available.');
  const logsResponse = page.waitForResponse(r => new URL(r.url()).pathname === '/api/logs' && r.request().method() === 'GET');
  await page.getByRole('navigation').getByRole('link', { name: 'Audit logs', exact: true }).click();
  const logs = await logsResponse; expect(logs.status()).toBe(200); const body = await logs.json() as AuditPage;
  expect(Object.keys(body)).toEqual(['items']); expect(body.items.length).toBeLessThanOrEqual(100);
  await expect(page).toHaveURL('/logs');
  if (body.items.length) {
    await expect(page.getByRole('columnheader')).toHaveCount(5);
    await expect(page.locator('tbody tr')).toHaveCount(body.items.length);
    for (const [index, item] of body.items.entries()) {
      expect(Object.keys(item).sort()).toEqual(item.user_id ? ['action', 'id', 'status', 'timestamp', 'user_id'] : ['action', 'id', 'status', 'timestamp']);
      const row = page.locator('tbody tr').nth(index);
      await expect(row.locator('td').nth(0)).toHaveText(item.id); await expect(row.locator('td').nth(1)).toHaveText(item.user_id ?? '');
      await expect(row.locator('td').nth(2)).toHaveText(item.action); await expect(row.locator('td').nth(3)).toHaveText(item.status);
      await expect(row.locator('time')).toHaveAttribute('datetime', item.timestamp);
    }
  } else await expect(page.getByRole('main')).toContainText('No audit records are available.');
  await expect(page.getByRole('main').locator('input,select,form,button')).toHaveCount(0);
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
});

// Ordinary users see no activity links and the real API independently rejects both endpoints.
test('User cannot access administrator activity', async ({ page }) => {
  let token = '';
  page.on('response', async response => {
    if (new URL(response.url()).pathname === '/api/login' && response.status() === 200) token = (await response.json() as { accessToken: string }).accessToken;
  });
  await page.goto('/');
  await page.getByRole('main').getByRole('link', { name: 'Sign in', exact: true }).click();
  await signIn(page, 'User');
  await expect(page.getByRole('navigation').getByRole('link', { name: 'Dashboard', exact: true })).toHaveCount(0);
  await expect(page.getByRole('navigation').getByRole('link', { name: 'Audit logs', exact: true })).toHaveCount(0);
  await expect.poll(() => token.length).toBeGreaterThan(0);
  for (const path of ['/api/logs', '/api/dashboard']) {
    const response = await page.request.get(path, { headers: { Authorization: `Bearer ${token}` } });
    expect(response.status()).toBe(403); const body = await response.json() as { status: number; type: string };
    expect(body.status).toBe(403); expect(body.type).toBe('access_denied'); expect(Object.keys(body).sort()).toEqual(['correlationId', 'errors', 'status', 'title', 'type']);
  }
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
});
