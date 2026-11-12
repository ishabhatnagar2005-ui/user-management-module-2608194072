/** Exercise native form interactions, safe HTTP transport, detail reads and real route wiring. */
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient, withInterceptors, HttpErrorResponse } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { RouterTestingHarness } from '@angular/router/testing';
import { UserFormComponent } from './user-form.component';
import { UserDetailComponent } from './user-detail.component';
import { UserApiService, accountError } from './user-api.service';
import { UserView } from '../core/api.models';
import { AuthService } from '../auth/auth.service';
import { authInterceptor } from '../auth/auth.interceptor';
import { NotificationService } from '../core/notification.service';
import { AppComponent } from '../app.component';
import { routes } from '../app.routes';
/** Supply safe component-only response data without mocking persistence in E2E. */
function account(): UserView { return { id: '000000000000000000000001', username: 'synthetic.user', city: 'Synthetic city', is_active: true, created_at: new Date().toISOString(), updated_at: new Date().toISOString() }; }

describe('Account form and transport', () => {
  let http: HttpTestingController;
  let fixture: ComponentFixture<UserFormComponent>;
  let auth: AuthService;
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [UserFormComponent, AppComponent], providers: [provideRouter(routes), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController); auth = TestBed.inject(AuthService);
    fixture = TestBed.createComponent(UserFormComponent); fixture.detectChanges();
  });
  afterEach(() => { fixture.destroy(); auth.clear(); http.verify(); });
  /** Drive native reactive controls, including blur-associated validation. */
  function enter(field: string, value: string): void {
    const input = fixture.nativeElement.querySelector(`#${field}`) as HTMLInputElement;
    input.value = value; input.dispatchEvent(new Event('input')); input.dispatchEvent(new Event('blur')); fixture.detectChanges();
  }
  /** Submit the actual rendered form rather than invoking a component method. */
  function submit(): void { fixture.nativeElement.querySelector('form').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); fixture.detectChanges(); }
  /** Establish the existing memory session through its actual HttpClient response lifecycle. */
  function login(role: 'Admin' | 'User'): void {
    auth.login({ username: 'synthetic.admin', password: 'synthetic-password' }).subscribe();
    http.expectOne('/api/login').flush({ accessToken: `header.${btoa(JSON.stringify({ role }))}.signature`, tokenType: 'Bearer', expiresAt: new Date(Date.now() + 900000).toISOString() });
  }
  it('renders all labelled fields with no creation on initialization and no role control', () => {
    http.expectNone('/api/users'); expect(fixture.nativeElement.querySelectorAll('input').length).toBe(8);
    expect(fixture.nativeElement.querySelector('#password').type).toBe('password');
    expect(fixture.nativeElement.querySelector('label[for=aadhaar_no]').textContent).toContain('Aadhaar');
    expect(fixture.nativeElement.querySelector('[formControlName=role]')).toBeNull();
    expect(fixture.nativeElement.querySelector('a').getAttribute('href')).toBe('/');
  });
  it('blocks missing fields and associates accessible errors', () => {
    submit(); http.expectNone('/api/users');
    expect(fixture.nativeElement.querySelector('#username').getAttribute('aria-invalid')).toBe('true');
    expect(fixture.nativeElement.querySelector('#username').getAttribute('aria-describedby')).toBe('username-error');
    expect(fixture.nativeElement.querySelectorAll('[role=alert]').length).toBe(2);
  });
  it('rejects username injection UTF-8 overflow and optional control characters', () => {
    enter('username', '<script>'); enter('password', 'é'.repeat(37)); enter('city', 'bad\u0001city'); submit();
    http.expectNone('/api/users'); expect(fixture.nativeElement.querySelectorAll('[role=alert]').length).toBe(3);
  });
  for (const [field, maximum] of [['address', 500], ['city', 100], ['country', 100], ['education', 200], ['aadhaar_no', 64], ['pan_no', 64]] as const) {
    it(`bounds optional ${field} and clears validation when corrected`, () => {
      enter('username', 'synthetic.user'); enter('password', 'synthetic-password'); enter(field, 'a'.repeat(maximum + 1)); submit(); http.expectNone('/api/users');
      expect(fixture.nativeElement.querySelector(`#${field}-error`)).not.toBeNull();
      enter(field, 'a'.repeat(maximum)); submit(); const request = http.expectOne('/api/users'); expect(request.request.body[field].length).toBe(maximum); request.flush({}, { status: 409, statusText: 'Conflict' });
    });
  }
  it('posts once with bearer on explicit submit shows loading and navigates to returned detail', fakeAsync(() => {
    login('Admin'); const navigation = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    enter('username', 'synthetic.user'); enter('password', 'é'.repeat(36)); enter('aadhaar_no', 'SYNTHETIC-ID'); submit(); submit();
    expect(fixture.nativeElement.querySelector('button').disabled).toBeTrue(); expect(fixture.nativeElement.querySelector('[role=status]').textContent).toContain('Saving');
    const request = http.expectOne('/api/users'); expect(request.request.method).toBe('POST'); expect(request.request.headers.get('Authorization')).toContain('Bearer ');
    expect(Object.keys(request.request.body).sort()).toEqual(['aadhaar_no', 'address', 'city', 'country', 'education', 'pan_no', 'password', 'username']);
    const result = account(); request.flush(result); fixture.detectChanges(); tick();
    expect(navigation).toHaveBeenCalledWith(['/users', result.id]); expect(TestBed.inject(NotificationService).message()).toContain('created successfully');
    expect(fixture.nativeElement.querySelector('#password').value).toBe(''); expect(fixture.nativeElement.querySelector('#aadhaar_no').value).toBe('');
  }));
  for (const status of [400, 401, 403, 409, 503, 0]) {
    it(`shows safe ${status} create failure and permits retry or sign-in recovery`, () => {
      const navigation = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
      enter('username', 'synthetic.user'); enter('password', 'synthetic-password'); enter('pan_no', 'SYNTHETIC-PAN'); submit();
      const request = http.expectOne('/api/users');
      if (status === 0) request.error(new ProgressEvent('network')); else request.flush({ title: 'secret-server-value' }, { status, statusText: 'Failure' });
      fixture.detectChanges(); expect(fixture.nativeElement.querySelector('[role=alert]').textContent).not.toContain('secret-server-value');
      expect(fixture.nativeElement.querySelector('[role=alert]').textContent).toBe(accountError(new HttpErrorResponse({ status })));
      expect(fixture.nativeElement.querySelector('button').disabled).toBeFalse(); expect(fixture.nativeElement.querySelector('#pan_no').value).toBe(''); if (status === 401) { expect(navigation).toHaveBeenCalledWith(['/login']); expect(auth.token()).toBeNull(); } else expect(navigation).not.toHaveBeenCalled();
      enter('password', 'synthetic-password'); submit(); http.expectOne('/api/users').flush({}, { status: 409, statusText: 'Conflict' });
    });
  }
  it('cancels outstanding create transport when the form is destroyed', () => {
    enter('username', 'synthetic.user'); enter('password', 'synthetic-password'); submit(); const request = http.expectOne('/api/users'); fixture.destroy(); expect(request.cancelled).toBeTrue();
  });
  it('encodes detail route data and propagates errors without fallback data', () => {
    const api = TestBed.inject(UserApiService); let result: UserView | undefined; let failure: unknown;
    api.read('../hostile').subscribe({ next: user => result = user, error: error => failure = error });
    const request = http.expectOne('/api/users/..%2Fhostile'); expect(request.request.method).toBe('GET'); request.flush({}, { status: 400, statusText: 'Bad Request' });
    expect(result).toBeUndefined(); expect(failure instanceof HttpErrorResponse).toBeTrue(); expect(accountError(new Error('secret'))).toContain('temporarily unavailable');
  });
  it('shows an Admin-only shell link which disappears for User and signed-out sessions', () => {
    login('Admin'); const shell = TestBed.createComponent(AppComponent); shell.detectChanges();
    expect(shell.nativeElement.querySelector('a[href="/users/new"]')).not.toBeNull();
    login('User'); shell.detectChanges(); expect(shell.nativeElement.querySelector('a[href="/users/new"]')).toBeNull();
    auth.clear(); shell.detectChanges(); expect(shell.nativeElement.querySelector('a[href="/users/new"]')).toBeNull(); shell.destroy();
  });
});

describe('Account detail reads', () => {
  let http: HttpTestingController;
  let fixture: ComponentFixture<UserDetailComponent>;
  const params = new BehaviorSubject(convertToParamMap({ id: '000000000000000000000001' }));
  beforeEach(() => {
    params.next(convertToParamMap({ id: '000000000000000000000001' }));
    TestBed.configureTestingModule({ imports: [UserDetailComponent], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: ActivatedRoute, useValue: { paramMap: params } }] });
    http = TestBed.inject(HttpTestingController); fixture = TestBed.createComponent(UserDetailComponent); fixture.detectChanges();
  });
  afterEach(() => { fixture.destroy(); http.verify(); });
  it('shows loading then safe data and missing optionals without any mutation request', () => {
    expect(fixture.nativeElement.querySelector('[role=status]').textContent).toContain('Loading');
    const request = http.expectOne('/api/users/000000000000000000000001'); expect(request.request.method).toBe('GET'); request.flush(account()); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('synthetic.user'); expect(fixture.nativeElement.textContent).toContain('Synthetic city');
    expect(fixture.nativeElement.textContent).toContain('Not provided'); expect(fixture.nativeElement.textContent).toContain('Active');
    expect(fixture.nativeElement.querySelector('input')).toBeNull(); expect(fixture.nativeElement.querySelector('button').textContent).toContain('Deactivate'); expect(fixture.nativeElement.querySelector('dialog').open).toBeFalse();
    http.expectNone(r => r.method !== 'GET');
  });
  for (const status of [400, 401, 403, 404, 503, 0]) {
    it(`renders safe ${status} read failure without stale account content`, () => {
      const request = http.expectOne('/api/users/000000000000000000000001');
      if (status === 0) request.error(new ProgressEvent('network')); else request.flush({ password: 'secret' }, { status, statusText: 'Failure' });
      fixture.detectChanges(); expect(fixture.nativeElement.querySelector('[role=alert]').textContent).toBe(accountError(new HttpErrorResponse({ status })));
      expect(fixture.nativeElement.querySelector('dl')).toBeNull(); expect(fixture.nativeElement.querySelector('[aria-busy]').getAttribute('aria-busy')).toBe('false');
    });
  }
  it('cancels superseded detail reads and permits a new route after a failure', () => {
    const first = http.expectOne('/api/users/000000000000000000000001'); params.next(convertToParamMap({ id: '000000000000000000000002' })); expect(first.cancelled).toBeTrue();
    http.expectOne('/api/users/000000000000000000000002').flush({}, { status: 404, statusText: 'Not Found' }); fixture.detectChanges(); expect(fixture.nativeElement.querySelector('[role=alert]')).not.toBeNull();
    params.next(convertToParamMap({ id: '000000000000000000000003' })); http.expectOne('/api/users/000000000000000000000003').flush({ ...account(), username: 'next.user', is_active: false }); fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role=alert]')).toBeNull(); expect(fixture.nativeElement.textContent).toContain('next.user'); expect(fixture.nativeElement.textContent).toContain('Inactive');
  });
});

describe('Real account route integration', () => {
  it('denies anonymous/User routes and loads actual form/detail for Admin', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController); const auth = TestBed.inject(AuthService); const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/users/new'); expect(TestBed.inject(Router).url).toBe('/login');
    /** Replace only component transport, leaving guards and route navigation real. */
    const login = (role: string): void => { auth.login({ username: 'synthetic.user', password: 'synthetic-password' }).subscribe(); http.expectOne('/api/login').flush({ accessToken: `h.${btoa(JSON.stringify({ role }))}.s`, tokenType: 'Bearer', expiresAt: new Date(Date.now() + 900000).toISOString() }); };
    login('User'); await harness.navigateByUrl('/users/new'); expect(TestBed.inject(Router).url).toBe('/login');
    login('Admin'); await harness.navigateByUrl('/users/new', UserFormComponent); expect(harness.routeNativeElement!.querySelector('form')).not.toBeNull(); http.expectNone('/api/users');
    await harness.navigateByUrl('/users/000000000000000000000001', UserDetailComponent);
    http.expectOne('/api/users/000000000000000000000001').flush(account()); harness.detectChanges(); expect(harness.routeNativeElement!.textContent).toContain('synthetic.user'); auth.clear(); http.verify();
  });
});
