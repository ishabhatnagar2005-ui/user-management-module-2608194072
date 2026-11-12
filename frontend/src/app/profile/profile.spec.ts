/** Verify profile forms, session lifecycle and routing using native Angular component behavior. */
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, HttpErrorResponse, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ProfileComponent } from './profile.component';
import { ProfileApiService, profileError } from './profile-api.service';
import { AuthService } from '../auth/auth.service';
import { authInterceptor } from '../auth/auth.interceptor';
import { NotificationService } from '../core/notification.service';
import { UserView } from '../core/api.models';
import { routes } from '../app.routes';
import { AppComponent } from '../app.component';
/** Supply safe responses; real persistence is exercised by the Mongo API tests. */
function account(): UserView { return { id: '000000000000000000000001', username: 'synthetic.user', address: 'Address', city: 'Old city', country: 'Country', education: 'Education', is_active: true, created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' }; }
/** Establish component-only sessions through the actual auth service response flow. */
function login(http: HttpTestingController, role = 'User'): void {
  TestBed.inject(AuthService).login({ username: 'synthetic.user', password: 'synthetic-password' }).subscribe();
  http.expectOne('/api/login').flush({ accessToken: `h.${btoa(JSON.stringify({ role }))}.s`, tokenType: 'Bearer', expiresAt: new Date(Date.now() + 900000).toISOString() });
}
describe('Native own profile', () => {
  let fixture: ComponentFixture<ProfileComponent>; let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [ProfileComponent], providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController); login(http); fixture = TestBed.createComponent(ProfileComponent); fixture.detectChanges();
  });
  afterEach(() => { fixture.destroy(); TestBed.inject(AuthService).clear(); http.verify(); });
  /** Complete the initial safe read and refresh native controls. */
  function loaded(): void { http.expectOne('/api/profile').flush(account()); fixture.detectChanges(); }
  /** Enter through labelled native reactive controls. */
  function enter(field: string, value: string): void { const input = fixture.nativeElement.querySelector('#' + field) as HTMLInputElement; input.value = value; input.dispatchEvent(new Event('input')); input.dispatchEvent(new Event('blur')); fixture.detectChanges(); }
  /** Submit the rendered native form rather than calling business methods. */
  function submit(password = false): void { fixture.nativeElement.querySelectorAll('form')[password ? 1 : 0].dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); fixture.detectChanges(); }
  it('loads only own safe fields with bearer and no render-time mutation', () => {
    expect(fixture.nativeElement.textContent).toContain('Loading profile'); expect(fixture.nativeElement.querySelector('form')).toBeNull();
    const request = http.expectOne('/api/profile'); expect(request.request.method).toBe('GET'); expect(request.request.headers.get('Authorization')).toContain('Bearer '); request.flush(account()); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('synthetic.user'); expect(fixture.nativeElement.querySelector('#city').value).toBe('Old city');
    for (const field of ['username', 'role', 'aadhaar_no', 'pan_no']) expect(fixture.nativeElement.querySelector('#' + field)).toBeNull();
    expect(fixture.nativeElement.querySelector('#currentPassword').type).toBe('password'); expect(fixture.nativeElement.querySelector('#currentPassword').autocomplete).toBe('current-password'); expect(fixture.nativeElement.querySelector('#newPassword').autocomplete).toBe('new-password'); http.expectNone(r => r.method !== 'GET');
  });
  it('saves exactly four fields once with loading and success while keeping the session', () => {
    loaded(); enter('city', 'New city'); enter('address', ''); submit(); submit(); const request = http.expectOne('/api/profile');
    expect(request.request.method).toBe('PUT'); expect(request.request.body).toEqual({ address: '', city: 'New city', country: 'Country', education: 'Education' }); expect(fixture.nativeElement.querySelector('button').disabled).toBeTrue(); expect(fixture.nativeElement.textContent).toContain('Saving profile securely');
    request.flush({ ...account(), city: 'New city' }); fixture.detectChanges(); expect(fixture.nativeElement.textContent).toContain('Profile updated successfully.'); expect(TestBed.inject(NotificationService).message()).toBe('Profile updated successfully.'); expect(TestBed.inject(AuthService).signedIn()).toBeTrue();
  });
  for (const [field, maximum] of [['address', 500], ['city', 100], ['country', 100], ['education', 200]] as const) {
    it(`rejects oversized ${field} with associated alert and no write`, () => { loaded(); enter(field, 'a'.repeat(maximum + 1)); submit(); http.expectNone(r => r.method !== 'GET'); expect(fixture.nativeElement.querySelector('#' + field).getAttribute('aria-invalid')).toBe('true'); expect(fixture.nativeElement.querySelector('#' + field).getAttribute('aria-describedby')).toBe(field + '-error'); expect(fixture.nativeElement.querySelector('#' + field + '-error').getAttribute('role')).toBe('alert'); });
    it(`accepts inclusive ${field} maximum`, () => { loaded(); enter(field, 'a'.repeat(maximum)); submit(); const request = http.expectOne('/api/profile'); expect(request.request.body[field].length).toBe(maximum); request.flush(account()); });
  }
  it('rejects control characters without any partial save', () => { loaded(); enter('city', 'bad\u0001city'); enter('address', 'Valid'); submit(); http.expectNone(r => r.method !== 'GET'); expect(fixture.nativeElement.querySelector('#city').getAttribute('aria-invalid')).toBe('true'); });
  for (const status of [400, 403, 404, 503, 0]) {
    it(`retains profile input and permits retry after ${status} save failure`, () => {
      loaded(); enter('city', 'Attempted city'); submit(); const request = http.expectOne('/api/profile'); if (!status) request.error(new ProgressEvent('network')); else request.flush({ secret: 'unsafe' }, { status, statusText: 'Failure' }); fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('[role=alert]').textContent).toBe(profileError(new HttpErrorResponse({ status }))); expect(fixture.nativeElement.textContent).not.toContain('unsafe'); expect(fixture.nativeElement.querySelector('#city').value).toBe('Attempted city'); expect(fixture.nativeElement.querySelector('button').disabled).toBeFalse(); submit(); http.expectOne('/api/profile').flush(account());
    });
    it(`blocks forms after ${status} read failure and recovers through retry`, () => {
      const request = http.expectOne('/api/profile'); if (!status) request.error(new ProgressEvent('network')); else request.flush({}, { status, statusText: 'Failure' }); fixture.detectChanges(); expect(fixture.nativeElement.querySelector('form')).toBeNull(); expect(fixture.nativeElement.querySelector('[role=alert]').textContent).toBe(profileError(new HttpErrorResponse({ status })));
      fixture.nativeElement.querySelector('button').click(); fixture.detectChanges(); loaded(); expect(fixture.nativeElement.querySelector('#city').value).toBe('Old city'); http.expectNone(r => r.method !== 'GET');
    });
    it(`erases secrets but retains session and unsaved profile after ${status} password failure`, () => {
      loaded(); const navigation = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true); const old = TestBed.inject(AuthService).token(); enter('city', 'Unsaved'); enter('currentPassword', 'synthetic-password'); enter('newPassword', 'new-valid-password'); submit(true);
      const request = http.expectOne('/api/profile/password'); if (!status) request.error(new ProgressEvent('network')); else request.flush({ password: 'unsafe' }, { status, statusText: 'Failure' }); fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('[role=alert]').textContent).toBe(profileError(new HttpErrorResponse({ status }), true)); expect(fixture.nativeElement.querySelector('#currentPassword').value).toBe(''); expect(fixture.nativeElement.querySelector('#newPassword').value).toBe(''); expect(fixture.nativeElement.querySelector('#city').value).toBe('Unsaved'); expect(TestBed.inject(AuthService).token()).toBe(old); expect(navigation).not.toHaveBeenCalled();
      enter('currentPassword', 'synthetic-password'); enter('newPassword', 'new-valid-password'); submit(true); http.expectOne('/api/profile/password').flush({}, { status: 400, statusText: 'Invalid' });
    });
  }
  for (const [field, value] of [['currentPassword', ''], ['currentPassword', 'short'], ['newPassword', ''], ['newPassword', 'a'.repeat(11)], ['newPassword', 'a'.repeat(73)], ['newPassword', 'é'.repeat(37)]] as const) {
    it(`blocks invalid ${field} byte length ${new TextEncoder().encode(value).length}`, () => { loaded(); enter('currentPassword', 'synthetic-password'); enter('newPassword', 'new-valid-password'); enter(field, value); submit(true); http.expectNone(r => r.method !== 'GET'); expect(fixture.nativeElement.querySelector('#' + field).getAttribute('aria-invalid')).toBe('true'); expect(fixture.nativeElement.querySelector('#' + field).getAttribute('aria-describedby')).toBe(field + '-error'); });
  }
  for (const value of ['a'.repeat(12), 'a'.repeat(72), 'é'.repeat(36)]) {
    it(`accepts inclusive ${new TextEncoder().encode(value).length} bytes and clears memory session after 204`, () => {
      loaded(); const navigation = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true); const old = TestBed.inject(AuthService).token(); enter('currentPassword', 'synthetic-password'); enter('newPassword', value); submit(true); submit(true);
      const request = http.expectOne('/api/profile/password'); expect(request.request.method).toBe('POST'); expect(request.request.body).toEqual({ currentPassword: 'synthetic-password', newPassword: value }); expect(request.request.headers.get('Authorization')).toBe('Bearer ' + old); expect(fixture.nativeElement.textContent).toContain('Verifying and changing password securely'); expect(fixture.nativeElement.querySelectorAll('button')[1].disabled).toBeTrue();
      request.flush(null, { status: 204, statusText: 'No Content' }); fixture.detectChanges(); const auth = TestBed.inject(AuthService); expect(auth.token()).toBeNull(); expect(auth.signedIn()).toBeFalse(); expect(auth.role()).toBeNull(); expect(navigation).toHaveBeenCalledWith(['/login']); expect(fixture.componentInstance.passwordForm.getRawValue()).toEqual({ currentPassword: '', newPassword: '' }); expect(fixture.nativeElement.querySelector('form')).toBeNull(); expect(TestBed.inject(NotificationService).message()).toBe('Password changed successfully. Sign in with your new password.');
    });
  }
  it('blocks profile submission while password change is pending', () => { loaded(); enter('currentPassword', 'synthetic-password'); enter('newPassword', 'new-valid-password'); submit(true); submit(); http.expectNone(r => r.method === 'PUT'); http.expectOne('/api/profile/password').flush({}, { status: 400, statusText: 'Invalid' }); });
  it('blocks password submission while profile save is pending', () => { loaded(); enter('currentPassword', 'synthetic-password'); enter('newPassword', 'new-valid-password'); submit(); submit(true); http.expectNone('/api/profile/password'); http.expectOne('/api/profile').flush(account()); });
  for (const operation of ['read', 'update', 'password']) {
    it(`clears rejected ${operation} session through the existing 401 interceptor`, () => { const navigation = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true); if (operation !== 'read') { loaded(); if (operation === 'password') { enter('currentPassword', 'synthetic-password'); enter('newPassword', 'new-valid-password'); } submit(operation === 'password'); } http.expectOne(operation === 'password' ? '/api/profile/password' : '/api/profile').flush({}, { status: 401, statusText: 'Unauthorized' }); expect(TestBed.inject(AuthService).token()).toBeNull(); expect(navigation).toHaveBeenCalledWith(['/login']); });
    it(`cancels outstanding ${operation} on destruction`, () => { if (operation !== 'read') { loaded(); if (operation === 'password') { enter('currentPassword', 'synthetic-password'); enter('newPassword', 'new-valid-password'); } submit(operation === 'password'); } const request = http.expectOne(operation === 'password' ? '/api/profile/password' : '/api/profile'); fixture.destroy(); expect(request.cancelled).toBeTrue(); });
  }
});
describe('Profile route and navigation', () => {
  it('redirects anonymous visitors and loads actual profile for User and Admin', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()] }); const http = TestBed.inject(HttpTestingController); const harness = await RouterTestingHarness.create(); await harness.navigateByUrl('/profile'); expect(TestBed.inject(Router).url).toBe('/login'); http.expectNone('/api/profile');
    for (const role of ['User', 'Admin']) { login(http, role); await harness.navigateByUrl('/profile', ProfileComponent); http.expectOne('/api/profile').flush(account()); harness.detectChanges(); expect(harness.routeNativeElement!.textContent).toContain('synthetic.user'); await harness.navigateByUrl('/'); TestBed.inject(AuthService).clear(); } http.verify();
  });
  it('shows My profile native navigation link only when signed in for both roles', () => {
    TestBed.configureTestingModule({ imports: [AppComponent], providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()] }); const http = TestBed.inject(HttpTestingController); const fixture = TestBed.createComponent(AppComponent); fixture.detectChanges(); expect(fixture.nativeElement.querySelector('a[href="/profile"]')).toBeNull();
    for (const role of ['User', 'Admin']) { login(http, role); fixture.detectChanges(); const link = fixture.nativeElement.querySelector('a[href="/profile"]'); expect(link.textContent).toBe('My profile'); expect(link.tagName).toBe('A'); TestBed.inject(AuthService).clear(); } fixture.destroy(); http.verify();
  });
  it('propagates all three transport errors without accepting a target identity', () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] }); const http = TestBed.inject(HttpTestingController); const api = TestBed.inject(ProfileApiService); let failures = 0;
    api.read().subscribe({ error: () => failures++ }); api.update({ city: null }).subscribe({ error: () => failures++ }); api.changePassword({ currentPassword: 'synthetic-password', newPassword: 'new-valid-password' }).subscribe({ error: () => failures++ });
    const requests = http.match(r => r.url.startsWith('/api/profile')); expect(requests.length).toBe(3); for (const request of requests) { expect(request.request.params.keys()).toEqual([]); request.flush({}, { status: 503, statusText: 'Unavailable' }); } expect(failures).toBe(3); http.verify();
  });
});
