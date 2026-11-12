/** Follow real authentication journeys by entering once, then clicking application links. */
import { test, expect, signIn, credentials } from './fixtures';
test('sign in, sign out and inert recovery use real servers', async ({ page }) => {
  const writes: string[] = [];
  page.on('request', request => { if (request.method() === 'POST') writes.push(new URL(request.url()).pathname); });
  await page.goto('/');
  await page.getByRole('main').getByRole('link', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Sign in', exact: true })).toBeVisible();
  expect(writes).toEqual([]);
  await signIn(page);
  const storage = await page.evaluate(() => ({ local: { ...localStorage }, session: { ...sessionStorage } }));
  expect(storage).toEqual({ local: {}, session: {} });
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByRole('heading', { name: 'Sign in', exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'Forgot password?' }).click();
  await expect(page.getByRole('heading', { name: 'Password recovery' })).toBeVisible();
  await expect(page.getByText('This page does not send email, verify identity or change your password.')).toBeVisible();
  expect(writes).toEqual(['/api/login']);
  await page.screenshot({ path: test.info().outputPath('recovery.png'), fullPage: true });
  await page.getByRole('link', { name: 'Back to sign in' }).click();
  await expect(page.getByRole('heading', { name: 'Sign in', exact: true })).toBeVisible();
});
test('wrong credentials are safely denied and the form can retry', async ({ page }) => {
  test.info().annotations.push({ type: 'expected-http-console-status', description: '401' });
  await page.goto('/login');
  await page.getByLabel('Username', { exact: true }).fill(credentials().username);
  await page.getByLabel('Password', { exact: true }).fill('synthetic-wrong-password');
  const denied = page.waitForResponse(r => new URL(r.url()).pathname === '/api/login' && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  const response = await denied; expect(response.status()).toBe(401);
  const body = await response.json() as { type: string; title: string };
  expect(body.type).toBe('authentication_failed'); expect(body.title).toBe('Credentials could not be verified');
  await expect(page.getByRole('alert')).toContainText('Credentials could not be verified.');
  await expect(page.getByLabel('Password', { exact: true })).toHaveValue('');
  await signIn(page);
  await page.getByRole('button', { name: 'Sign out' }).click();
});
