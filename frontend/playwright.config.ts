/** Start both real tiers; Mongo must already be primary. E2E is executed only by the parent. */
import { defineConfig, devices } from '@playwright/test';
import path from 'node:path';
import os from 'node:os';
const frontend = process.env['E2E_FRONTEND_URL'] || 'http://127.0.0.1:4200';
const backend = process.env['E2E_BACKEND_URL'] || 'http://127.0.0.1:5088';
const dotnet = process.env['DOTNET_BIN'] || path.join(os.homedir(), '.dotnet', 'dotnet');
const frontendAddress = new URL(frontend);
export default defineConfig({
  testDir: './e2e', fullyParallel: false, workers: 1, retries: 0, timeout: 30000,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: { baseURL: frontend, trace: 'retain-on-failure', screenshot: 'only-on-failure', launchOptions: { args: ['--no-sandbox', '--disable-dev-shm-usage'] } },
  projects: [
    { name: 'desktop-chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'mobile-chromium', use: { ...devices['Pixel 7'] } }
  ],
  webServer: [
    { command: `"${dotnet}" run --project ../backend/UserManagement.csproj --no-launch-profile`, url: `${backend}/api/health`,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: backend }, reuseExistingServer: false, timeout: 90000 },
    { command: `node node_modules/@angular/cli/bin/ng.js serve --host ${frontendAddress.hostname} --port ${frontendAddress.port || '80'}`,
      url: frontend, env: { API_PROXY_TARGET: backend }, reuseExistingServer: false, timeout: 90000 }
  ]
});
