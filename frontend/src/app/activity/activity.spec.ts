/** Verify native activity rendering, safe failure states, transport and Admin navigation. */
import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { AuditComponent } from './audit.component';
import { DashboardComponent } from './dashboard.component';
import { activityError } from './activity-api.service';
import { AuditView, DashboardView } from '../core/api.models';
import { AuthService } from '../auth/auth.service';
import { authInterceptor } from '../auth/auth.interceptor';
import { routes } from '../app.routes';
import { AppComponent } from '../app.component';
/** Supply safe component-only HTTP data; Mongo tests own persistence verification. */
function record(): AuditView { return { id: '000000000000000000000001', action: 'login', status: 'failure', timestamp: '2020-01-02T03:04:05.006Z' }; }
/** Provide six source-shaped summaries for rendering assertions. */
function summary(): DashboardView { return { total: 5, active: 4, inactive: 1, recentLogins: [record()], countryDistribution: [{ country: 'Country A', count: 3 }], auditSummary: [{ action: 'login', success: 2, failure: 1 }] }; }
/** Establish component-only sessions through the real auth service response flow. */
function login(http: HttpTestingController, role: 'Admin' | 'User'): void {
  TestBed.inject(AuthService).login({ username: 'synthetic', password: 'synthetic-password' }).subscribe();
  http.expectOne('/api/login').flush({ accessToken: `h.${btoa(JSON.stringify({ role }))}.s`, tokenType: 'Bearer', expiresAt: new Date(Date.now() + 900000).toISOString() });
}
for (const [component, path, title] of [[AuditComponent, '/api/logs', 'Audit logs'], [DashboardComponent, '/api/dashboard', 'Dashboard']] as const) {
  describe(`Native ${title}`, () => {
    let http: HttpTestingController;
    beforeEach(() => { TestBed.configureTestingModule({ imports: [component], providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()] }); http = TestBed.inject(HttpTestingController); login(http, 'Admin'); });
    afterEach(() => { TestBed.inject(AuthService).clear(); http.verify(); });
    it('loads bearer GET once and has no extra audit controls', () => {
      const fixture = TestBed.createComponent<AuditComponent | DashboardComponent>(component); fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('h1').textContent).toBe(title); expect(fixture.nativeElement.querySelector('[role=status]').textContent).toContain('Loading');
      const request = http.expectOne(path); expect(request.request.method).toBe('GET'); expect(request.request.params.keys()).toEqual([]); expect(request.request.headers.get('Authorization')).toContain('Bearer ');
      request.flush(component === AuditComponent ? { items: [record(), { ...record(), id: '000000000000000000000002', user_id: '000000000000000000000003' }] } : summary()); fixture.detectChanges();
      expect(fixture.nativeElement.textContent).toContain('failure'); expect(fixture.nativeElement.querySelector('time').getAttribute('datetime')).toBe(record().timestamp);
      expect(fixture.nativeElement.querySelector('time').textContent).toBe('2020-01-02 03:04:05.006');
      expect(fixture.nativeElement.textContent).not.toContain('Unknown actor'); expect(fixture.nativeElement.querySelector('input,select,form')).toBeNull(); expect(fixture.nativeElement.querySelectorAll('button').length).toBe(0);
      if (component === AuditComponent) { expect(fixture.nativeElement.querySelectorAll('th[scope=col]').length).toBe(5); expect(fixture.nativeElement.querySelector('tbody tr td:nth-child(2)').textContent).toBe(''); }
      else { expect(fixture.nativeElement.querySelectorAll('h2').length).toBe(6); expect(fixture.nativeElement.querySelector('[data-testid=total]').textContent).toBe('5'); expect(fixture.nativeElement.querySelector('[data-testid=active]').textContent).toBe('4'); expect(fixture.nativeElement.querySelector('[data-testid=inactive]').textContent).toBe('1'); expect(fixture.nativeElement.textContent).toContain('Country A: 3'); expect(fixture.nativeElement.textContent).toContain('login: 2 success, 1 failure'); }
      http.expectNone(r => r.method !== 'GET'); fixture.destroy();
    });
    it('shows truthful empty states rather than samples', () => {
      const fixture = TestBed.createComponent<AuditComponent | DashboardComponent>(component); fixture.detectChanges(); http.expectOne(path).flush(component === AuditComponent ? { items: [] } : { total: 0, active: 0, inactive: 0, recentLogins: [], countryDistribution: [], auditSummary: [] }); fixture.detectChanges();
      expect(fixture.nativeElement.textContent).toContain(component === AuditComponent ? 'No audit records are available.' : 'No accounts are available.');
      if (component === DashboardComponent) { for (const message of ['No login records', 'No populated countries', 'No audit activity']) expect(fixture.nativeElement.textContent).toContain(message); expect(fixture.nativeElement.querySelector('[data-testid=total]').textContent).toBe('0'); }
      expect(fixture.nativeElement.querySelector('time')).toBeNull(); fixture.destroy();
    });
    for (const status of [401, 403, 503, 0, 500]) {
      it(`shows safe ${status} failure and recovers through native retry`, () => {
        const fixture = TestBed.createComponent<AuditComponent | DashboardComponent>(component); fixture.detectChanges(); const request = http.expectOne(path);
        if (status === 401) spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
        if (status) request.flush({ title: 'PRIVATE', password: 'PRIVATE' }, { status, statusText: 'Failure' }); else request.error(new ProgressEvent('network'));
        fixture.detectChanges(); expect(fixture.nativeElement.querySelector('[role=alert]').textContent).toBe(activityError(new HttpErrorResponse({ status }))); expect(fixture.nativeElement.textContent).not.toContain('PRIVATE'); expect(fixture.nativeElement.querySelector('table,[data-testid=total]')).toBeNull();
        fixture.nativeElement.querySelector('button').click(); fixture.detectChanges(); expect(fixture.nativeElement.querySelector('[role=status]').textContent).toContain('Loading'); expect(fixture.nativeElement.querySelector('[role=alert]')).toBeNull();
        http.expectOne(path).flush(component === AuditComponent ? { items: [record()] } : summary()); fixture.detectChanges(); expect(fixture.nativeElement.textContent).toContain('failure'); fixture.destroy();
      });
    }
    it('clears previous data before another read fails', () => {
      const fixture = TestBed.createComponent<AuditComponent | DashboardComponent>(component); http.expectOne(path).flush(component === AuditComponent ? { items: [record()] } : summary()); fixture.detectChanges();
      fixture.componentInstance.load(); fixture.detectChanges(); expect(fixture.nativeElement.querySelector('table,[data-testid=total]')).toBeNull(); http.expectOne(path).flush({}, { status: 503, statusText: 'Unavailable' }); fixture.detectChanges(); expect(fixture.nativeElement.querySelector('time')).toBeNull(); fixture.destroy();
    });
    it('cancels an outstanding read on destruction', () => { const fixture = TestBed.createComponent<AuditComponent | DashboardComponent>(component); const request = http.expectOne(path); fixture.destroy(); expect(request.cancelled).toBeTrue(); });
  });
}
describe('Activity routing and shell', () => {
  let http: HttpTestingController;
  beforeEach(() => { TestBed.configureTestingModule({ imports: [AppComponent], providers: [provideRouter(routes), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()] }); http = TestBed.inject(HttpTestingController); });
  afterEach(() => { TestBed.inject(AuthService).clear(); http.verify(); });
  for (const role of ['Admin', 'User'] as const) {
    it(`shows activity navigation only when ${role} is Admin`, () => { login(http, role); const fixture = TestBed.createComponent(AppComponent); fixture.detectChanges(); expect(fixture.nativeElement.querySelectorAll('a[href="/dashboard"],a[href="/logs"]').length).toBe(role === 'Admin' ? 2 : 0); fixture.destroy(); });
  }
  for (const path of ['/logs', '/dashboard']) {
    for (const role of ['User', null] as const) {
      it(`denies ${role ?? 'anonymous'} route ${path} before transport`, async () => { if (role) login(http, role); const harness = await RouterTestingHarness.create(); await harness.navigateByUrl(path); expect(TestBed.inject(Router).url).toBe('/login'); http.expectNone('/api/logs'); http.expectNone('/api/dashboard'); });
    }
    it(`allows Admin route ${path}`, async () => { login(http, 'Admin'); const harness = await RouterTestingHarness.create(); await harness.navigateByUrl(path); http.expectOne('/api' + path).flush(path === '/logs' ? { items: [record()] } : summary()); harness.detectChanges(); expect(harness.routeNativeElement?.textContent).toContain('failure'); });
  }
});
