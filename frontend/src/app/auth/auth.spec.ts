/** Exercise actual forms, error rendering, memory sessions and native HttpClient interception. */
import { TestBed, ComponentFixture, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient, withInterceptors, HttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter, Router, ActivatedRouteSnapshot, RouterStateSnapshot, UrlTree } from '@angular/router';
import { AuthService, authGuard, adminGuard } from './auth.service';
import { authInterceptor } from './auth.interceptor';
import { LoginComponent } from './login.component';
import { ForgotPasswordComponent } from './forgot-password.component';
import { AppComponent } from '../app.component';
import { HomeComponent } from '../home/home.component';
import { NotificationService } from '../core/notification.service';
import { LoginResult } from '../core/api.models';

/** Generate synthetic response data for component-only transport isolation, never E2E. */
function session(role = 'User', milliseconds = 900000): LoginResult {
  return { accessToken: `header.${btoa(JSON.stringify({ role }))}.signature`, tokenType: 'Bearer', expiresAt: new Date(Date.now() + milliseconds).toISOString() };
}

describe('Authentication forms and sessions', () => {
  let http: HttpTestingController;
  let auth: AuthService;
  let router: Router;
  let fixture: ComponentFixture<LoginComponent>;
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [LoginComponent, ForgotPasswordComponent, AppComponent, HomeComponent], providers: [
      provideRouter([{ path: '', component: HomeComponent }, { path: 'login', component: LoginComponent }, { path: 'forgot-password', component: ForgotPasswordComponent }]),
      provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()
    ] });
    http = TestBed.inject(HttpTestingController); auth = TestBed.inject(AuthService); router = TestBed.inject(Router);
    fixture = TestBed.createComponent(LoginComponent); fixture.detectChanges();
  });
  afterEach(() => { auth.clear(); http.verify(); });
  /** Enter values through native controls so form directives are genuinely exercised. */
  function enter(username: string, password: string): void {
    for (const [id, value] of [['username', username], ['password', password]]) {
      const input = fixture.nativeElement.querySelector(`#${id}`) as HTMLInputElement;
      input.value = value; input.dispatchEvent(new Event('input')); input.dispatchEvent(new Event('blur'));
    }
    fixture.detectChanges();
  }
  /** Submit the rendered form rather than invoking component internals. */
  function submit(): void { fixture.nativeElement.querySelector('form').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); fixture.detectChanges(); }
  /** Establish a component-only transport response and leave the real service lifecycle intact. */
  function login(role = 'User', milliseconds = 900000): string {
    const result = session(role, milliseconds); auth.login({ username: 'synthetic.user', password: 'synthetic-password' }).subscribe(); http.expectOne('/api/login').flush(result); return result.accessToken;
  }
  it('renders labels and recovery navigation without login requests', () => {
    http.expectNone('/api/login');
    expect(fixture.nativeElement.querySelector('label[for=username]').textContent).toContain('Username');
    expect(fixture.nativeElement.querySelector('input[type=password]').getAttribute('autocomplete')).toBe('current-password');
    expect(fixture.nativeElement.querySelector('a').getAttribute('href')).toBe('/forgot-password');
    expect(fixture.nativeElement.textContent).not.toContain('development.admin');
  });
  it('blocks empty and malformed fields with associated accessible errors', () => {
    submit(); http.expectNone('/api/login');
    expect(fixture.nativeElement.querySelector('#username').getAttribute('aria-invalid')).toBe('true');
    expect(fixture.nativeElement.querySelector('#username').getAttribute('aria-describedby')).toBe('username-error');
    enter('<script>', 'short'); submit(); http.expectNone('/api/login');
    expect(fixture.nativeElement.querySelectorAll('[role=alert]').length).toBe(2);
  });
  it('uses UTF-8 byte boundaries and permits paste-compatible multibyte secrets', () => {
    enter('synthetic.user', 'é'.repeat(37)); submit(); http.expectNone('/api/login');
    expect(fixture.nativeElement.querySelector('#password-error')).not.toBeNull();
    enter('synthetic.user', 'é'.repeat(36)); submit();
    const request = http.expectOne('/api/login'); expect(request.request.body.password).toBe('é'.repeat(36)); request.flush({}, { status: 401, statusText: 'Unauthorized' });
  });
  it('posts once on explicit submit shows busy state and returns to the entry after success', fakeAsync(() => {
    const navigation = spyOn(router, 'navigate').and.resolveTo(true);
    enter('synthetic.user', 'synthetic-password'); submit(); submit();
    expect(fixture.nativeElement.querySelector('button').disabled).toBeTrue();
    expect(fixture.nativeElement.querySelector('[role=status]').textContent).toContain('Verifying');
    const request = http.expectOne('/api/login'); expect(request.request.method).toBe('POST'); expect(request.request.headers.has('Authorization')).toBeFalse();
    const result = session('Admin'); request.flush(result); fixture.detectChanges(); tick();
    expect(auth.token()).toBe(result.accessToken); expect(auth.role()).toBe('Admin');
    expect(navigation).toHaveBeenCalledWith(['/']); expect(fixture.nativeElement.querySelector('#password').value).toBe('');
    expect(localStorage.getItem('accessToken')).toBeNull(); expect(sessionStorage.getItem('accessToken')).toBeNull();
  }));
  for (const status of [400, 401, 503, 0]) {
    it(`shows a safe ${status} failure without redirecting and allows retry`, () => {
      const navigation = spyOn(router, 'navigate').and.resolveTo(true);
      enter('synthetic.user', 'synthetic-password'); submit();
      const request = http.expectOne('/api/login');
      if (status === 0) request.error(new ProgressEvent('network error')); else request.flush({ title: 'sensitive-server-detail' }, { status, statusText: 'Failure' });
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('[role=alert]').textContent).not.toContain('sensitive-server-detail');
      expect(fixture.nativeElement.querySelector('button').disabled).toBeFalse(); expect(navigation).not.toHaveBeenCalled(); expect(auth.token()).toBeNull();
      enter('synthetic.user', 'synthetic-password'); submit(); http.expectOne('/api/login').flush({}, { status: 401, statusText: 'Unauthorized' });
    });
  }
  it('rejects malformed session responses and leaves no authenticated state', () => {
    enter('synthetic.user', 'synthetic-password'); submit();
    http.expectOne('/api/login').flush({ accessToken: 'invalid', tokenType: 'Bearer', expiresAt: new Date(Date.now() + 10000).toISOString() });
    fixture.detectChanges(); expect(auth.signedIn()).toBeFalse(); expect(fixture.nativeElement.querySelector('[role=alert]').textContent).toContain('temporarily unavailable');
  });
  it('attaches bearer only to protected same-origin API requests and expires on protected 401', () => {
    spyOn(router, 'navigate').and.resolveTo(true); const token = login();
    const client = TestBed.inject(HttpClient);
    client.get('/api/protected').subscribe({ error: () => {} });
    const protectedRequest = http.expectOne('/api/protected'); expect(protectedRequest.request.headers.get('Authorization')).toBe(`Bearer ${token}`);
    protectedRequest.flush({}, { status: 401, statusText: 'Unauthorized' }); expect(auth.token()).toBeNull();
    expect(TestBed.inject(NotificationService).message()).toContain('session ended');
    login();
    for (const url of ['/api/health', 'https://unrelated.invalid/api/users', '/api-other/users']) {
      client.get(url).subscribe({ error: () => {} }); const request = http.expectOne(url); expect(request.request.headers.has('Authorization')).toBeFalse(); request.flush({}, { status: 401, statusText: 'Unauthorized' });
    }
    expect(auth.token()).not.toBeNull();
  });
  it('keeps a current session on protected 403 and dependency failures', () => {
    const token = login(); const client = TestBed.inject(HttpClient);
    for (const status of [403, 503]) { client.get('/api/protected').subscribe({ error: () => {} }); http.expectOne('/api/protected').flush({}, { status, statusText: 'Failure' }); expect(auth.token()).toBe(token); }
  });
  it('expires at the configured response deadline and clears role and feedback', fakeAsync(() => {
    spyOn(router, 'navigate').and.resolveTo(true); login('Admin', 1000); tick(1001);
    expect(auth.token()).toBeNull(); expect(auth.role()).toBeNull(); expect(auth.signedIn()).toBeFalse(); expect(TestBed.inject(NotificationService).message()).toContain('session ended');
  }));
  it('clears a replaced identity and sign-out updates the actual shell', () => {
    spyOn(router, 'navigate').and.resolveTo(true); login('Admin'); const shell = TestBed.createComponent(AppComponent); shell.detectChanges();
    expect(shell.nativeElement.querySelector('button').textContent).toContain('Sign out');
    shell.nativeElement.querySelector('button').click(); shell.detectChanges(); expect(auth.token()).toBeNull(); expect(auth.role()).toBeNull();
    expect(shell.nativeElement.textContent).toContain('You have signed out.'); expect(shell.nativeElement.querySelector('button')).toBeNull();
    login('User'); expect(auth.role()).toBe('User'); expect(TestBed.inject(NotificationService).message()).toBe(''); shell.destroy();
  });
  it('redirects unauthenticated guards and denies User from the Admin guard', () => {
    const run = (guard: typeof authGuard) => TestBed.runInInjectionContext(() => guard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));
    expect(run(authGuard) instanceof UrlTree).toBeTrue(); login('User'); expect(run(authGuard)).toBeTrue(); expect(run(adminGuard) instanceof UrlTree).toBeTrue();
    login('Admin'); expect(run(adminGuard)).toBeTrue();
  });
  it('recovery remains inert and has only a return link', () => {
    const recovery = TestBed.createComponent(ForgotPasswordComponent); recovery.detectChanges();
    expect(recovery.nativeElement.textContent).toContain('does not send email'); expect(recovery.nativeElement.querySelector('form')).toBeNull(); expect(recovery.nativeElement.querySelector('input')).toBeNull();
    expect(recovery.nativeElement.querySelector('a').getAttribute('href')).toBe('/login'); http.expectNone(() => true); recovery.destroy();
  });
  it('cancels an outstanding form request when the component is destroyed', () => {
    enter('synthetic.user', 'synthetic-password'); submit(); const request = http.expectOne('/api/login'); fixture.destroy(); expect(request.cancelled).toBeTrue(); expect(auth.token()).toBeNull();
  });
});
