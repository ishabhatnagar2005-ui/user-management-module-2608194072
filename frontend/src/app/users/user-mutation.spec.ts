/** Verify native edit and confirmation behavior with real routing and component HTTP boundaries. */
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, HttpErrorResponse } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { BehaviorSubject } from 'rxjs';
import { UserFormComponent } from './user-form.component';
import { UserDetailComponent } from './user-detail.component';
import { UserApiService, accountError } from './user-api.service';
import { UserView } from '../core/api.models';
import { NotificationService } from '../core/notification.service';
import { AuthService } from '../auth/auth.service';
import { routes } from '../app.routes';
/** Use safe component responses; persistence is exercised separately by real Mongo API tests. */
function account(): UserView { return { id: '000000000000000000000001', username: 'synthetic.user', address: 'Address', city: 'Old city', country: 'Country', education: 'Education', is_active: true, created_at: new Date().toISOString(), updated_at: new Date().toISOString() }; }
const url = '/api/users/000000000000000000000001';

describe('Native directory edit', () => {
  let fixture: ComponentFixture<UserFormComponent>; let http: HttpTestingController;
  let params: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  beforeEach(() => {
    params = new BehaviorSubject(convertToParamMap({ id: account().id }));
    TestBed.configureTestingModule({ imports: [UserFormComponent], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: ActivatedRoute, useValue: { paramMap: params } }] });
    http = TestBed.inject(HttpTestingController); fixture = TestBed.createComponent(UserFormComponent); fixture.detectChanges();
  });
  afterEach(() => { fixture.destroy(); http.verify(); });
  /** Enter through labelled native reactive inputs. */
  function enter(field: string, value: string): void { const input = fixture.nativeElement.querySelector('#' + field) as HTMLInputElement; input.value = value; input.dispatchEvent(new Event('input')); input.dispatchEvent(new Event('blur')); fixture.detectChanges(); }
  /** Trigger the actual submit event and refresh visible feedback. */
  function submit(): void { fixture.nativeElement.querySelector('form').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); fixture.detectChanges(); }
  it('loads safe fields and renders no credentials or write on render', () => {
    expect(fixture.nativeElement.querySelector('button').disabled).toBeTrue(); submit(); http.expectNone(r => r.method !== 'GET');
    http.expectOne(url).flush(account()); fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('h1').textContent).toBe('Edit account');
    expect(fixture.nativeElement.querySelector('#password')).toBeNull(); expect(fixture.nativeElement.querySelector('#city').value).toBe('Old city');
    expect(fixture.nativeElement.querySelector('#pan_no').value).toBe(''); expect(fixture.nativeElement.querySelector('a').getAttribute('href')).toBe('/users/' + account().id);
    http.expectNone(r => r.method !== 'GET');
  });
  it('sends one allowlisted PUT keeps omitted identifiers and announces success', () => {
    const navigation = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true); http.expectOne(url).flush(account()); fixture.detectChanges();
    enter('city', 'New city'); submit(); submit(); expect(fixture.nativeElement.querySelector('button').disabled).toBeTrue();
    const request = http.expectOne(url); expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ username: 'synthetic.user', address: 'Address', city: 'New city', country: 'Country', education: 'Education' });
    request.flush({ ...account(), city: 'New city' }); fixture.detectChanges(); expect(navigation).toHaveBeenCalledWith(['/users', account().id]);
    expect(TestBed.inject(NotificationService).message()).toBe('Account updated successfully.');
  });
  it('allows explicit replacement identifiers but never submits password role or status', () => {
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true); http.expectOne(url).flush(account()); fixture.detectChanges(); enter('pan_no', 'SYNTHETIC-PAN'); submit();
    const request = http.expectOne(url); expect(request.request.body.pan_no).toBe('SYNTHETIC-PAN'); expect(request.request.body.password).toBeUndefined(); expect(request.request.body.role).toBeUndefined(); expect(request.request.body.is_active).toBeUndefined(); request.flush(account());
  });
  for (const [field, maximum] of [['username', 64], ['address', 500], ['city', 100], ['country', 100], ['education', 200], ['aadhaar_no', 64], ['pan_no', 64]] as const) {
    it(`rejects oversized ${field} without any write`, () => { http.expectOne(url).flush(account()); fixture.detectChanges(); enter(field, 'a'.repeat(maximum + 1)); submit(); http.expectNone(r => r.method !== 'GET'); expect(fixture.nativeElement.querySelector('#' + field).getAttribute('aria-invalid')).toBe('true'); });
  }
  it('rejects injection and control characters with associated errors', () => { http.expectOne(url).flush(account()); fixture.detectChanges(); enter('username', '../admin'); enter('city', 'bad\u0001city'); submit(); http.expectNone(r => r.method !== 'GET'); expect(fixture.nativeElement.querySelector('#username').getAttribute('aria-describedby')).toBe('username-error'); });
  for (const status of [400, 401, 403, 404, 409, 503, 0]) {
    it(`shows safe ${status} update error retains public input clears identifiers and permits retry`, () => {
      http.expectOne(url).flush(account()); fixture.detectChanges(); enter('city', 'Attempted city'); enter('pan_no', 'SYNTHETIC'); submit(); const request = http.expectOne(url);
      if (status === 0) request.error(new ProgressEvent('network')); else request.flush({ secret: 'raw-error' }, { status, statusText: 'Failure' }); fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('[role=alert]').textContent).toBe(accountError(new HttpErrorResponse({ status })));
      expect(fixture.nativeElement.querySelector('#city').value).toBe('Attempted city'); expect(fixture.nativeElement.querySelector('#pan_no').value).toBe(''); expect(fixture.nativeElement.querySelector('button').disabled).toBeFalse();
      submit(); http.expectOne(url).flush({}, { status: 409, statusText: 'Conflict' });
    });
  }
  for (const status of [400, 401, 403, 404, 503]) {
    it(`blocks submission after ${status} initial read failure`, () => { http.expectOne(url).flush({}, { status, statusText: 'Failure' }); fixture.detectChanges(); submit(); http.expectNone(r => r.method !== 'GET'); expect(fixture.nativeElement.querySelector('button').disabled).toBeTrue(); expect(fixture.nativeElement.querySelector('[role=alert]')).not.toBeNull(); });
  }
  it('cancels superseded reads and allows route recovery after failure', () => { const first = http.expectOne(url); params.next(convertToParamMap({ id: '000000000000000000000002' })); expect(first.cancelled).toBeTrue(); http.expectOne('/api/users/000000000000000000000002').flush({}, { status: 404, statusText: 'Missing' }); params.next(convertToParamMap({ id: account().id })); http.expectOne(url).flush(account()); fixture.detectChanges(); expect(fixture.nativeElement.querySelector('#username').value).toBe('synthetic.user'); });
  it('cancels outstanding mutation on destruction', () => { http.expectOne(url).flush(account()); fixture.detectChanges(); submit(); const request = http.expectOne(url); fixture.destroy(); expect(request.cancelled).toBeTrue(); });
});

describe('Native deactivation confirmation', () => {
  let fixture: ComponentFixture<UserDetailComponent>; let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [UserDetailComponent], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: ActivatedRoute, useValue: { paramMap: new BehaviorSubject(convertToParamMap({ id: account().id })) } }] });
    http = TestBed.inject(HttpTestingController); fixture = TestBed.createComponent(UserDetailComponent); fixture.detectChanges(); http.expectOne(url).flush(account()); fixture.detectChanges(); document.body.appendChild(fixture.nativeElement);
  });
  afterEach(() => { fixture.destroy(); http.verify(); });
  /** Activate the trigger with actual focus so close restoration can be verified. */
  function open(): HTMLDialogElement { const trigger = fixture.nativeElement.querySelector('button') as HTMLButtonElement; trigger.focus(); trigger.click(); fixture.detectChanges(); return fixture.nativeElement.querySelector('dialog'); }
  /** Confirm through the native action button, not component internals. */
  function confirm(): void { (fixture.nativeElement.querySelectorAll('dialog button')[1] as HTMLButtonElement).click(); fixture.detectChanges(); }
  it('renders an accessible edit link and no mutation before explicit confirmation', () => { expect(fixture.nativeElement.querySelector('a').getAttribute('href')).toBe('/users/' + account().id + '/edit'); expect(fixture.nativeElement.querySelector('dialog').open).toBeFalse(); http.expectNone(r => r.method !== 'GET'); });
  it('cancel and Escape close restore focus and do not send requests', () => {
    const dialog = open(); expect(dialog.open).toBeTrue(); expect(dialog.getAttribute('aria-labelledby')).toBe('confirm-title'); expect(document.body.style.overflow).toBe('hidden');
    expect(dialog.contains(document.activeElement)).toBeTrue(); (dialog.querySelector('button') as HTMLButtonElement).click(); fixture.detectChanges(); expect(dialog.open).toBeFalse(); expect(document.activeElement).toBe(fixture.nativeElement.querySelector('button'));
    open(); dialog.dispatchEvent(new Event('cancel', { cancelable: true })); fixture.detectChanges(); expect(dialog.open).toBeFalse(); http.expectNone(r => r.method !== 'GET');
  });
  it('confirms once shows disabled loading then retained inactive detail and success', () => {
    const dialog = open(); confirm(); confirm(); const request = http.expectOne(url); expect(request.request.method).toBe('DELETE'); expect(request.request.body).toBeNull();
    expect((dialog.querySelector('button') as HTMLButtonElement).disabled).toBeTrue(); expect(dialog.textContent).toContain('Deactivating');
    request.flush({ ...account(), is_active: false }); fixture.detectChanges(); expect(dialog.open).toBeFalse(); expect(fixture.nativeElement.textContent).toContain('Inactive'); expect(fixture.nativeElement.textContent).toContain('Account deactivated successfully.'); expect(fixture.nativeElement.querySelector('section > button')).toBeNull();
  });
  for (const status of [400, 401, 403, 404, 503, 0]) {
    it(`shows safe ${status} failure and keeps the active account for retry or cancel`, () => { const dialog = open(); confirm(); const request = http.expectOne(url); if (status === 0) request.error(new ProgressEvent('network')); else request.flush({ secret: 'unsafe' }, { status, statusText: 'Failure' }); fixture.detectChanges(); expect(dialog.open).toBeTrue(); expect(dialog.querySelector('[role=alert]')!.textContent).toBe(accountError(new HttpErrorResponse({ status }))); expect((dialog.querySelector('button') as HTMLButtonElement).disabled).toBeFalse(); expect(fixture.nativeElement.querySelector('dl').textContent).toContain('Active'); confirm(); http.expectOne(url).flush({}, { status: 503, statusText: 'Unavailable' }); });
  }
  it('prevents Escape cancellation while a submitted mutation is pending', () => { const dialog = open(); confirm(); dialog.dispatchEvent(new Event('cancel', { cancelable: true })); expect(dialog.open).toBeTrue(); http.expectOne(url).flush({ ...account(), is_active: false }); });
  it('restores body scroll on destruction and cancels pending transport', () => { open(); confirm(); const request = http.expectOne(url); fixture.destroy(); expect(request.cancelled).toBeTrue(); expect(document.body.style.overflow).not.toBe('hidden'); });
  it('does not offer reactivation or deactivation for an inactive record', () => { fixture.componentInstance.user.set({ ...account(), is_active: false }); fixture.detectChanges(); expect(fixture.nativeElement.querySelector('section > button')).toBeNull(); http.expectNone(r => r.method !== 'GET'); });
});

describe('Mutation route and transport boundaries', () => {
  it('encodes route identities for PUT/DELETE and propagates dependency failures', () => { TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] }); const http = TestBed.inject(HttpTestingController); const api = TestBed.inject(UserApiService); let failures = 0; api.update('../unsafe', { city: 'City' }).subscribe({ error: () => failures++ }); api.deactivate('../unsafe').subscribe({ error: () => failures++ }); for (const request of http.match('/api/users/..%2Funsafe')) request.flush({}, { status: 503, statusText: 'Unavailable' }); expect(failures).toBe(2); http.verify(); });
  it('guards edit for anonymous/User and loads the actual edit component for Admin', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()] }); const http = TestBed.inject(HttpTestingController); const auth = TestBed.inject(AuthService); const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/users/' + account().id + '/edit'); expect(TestBed.inject(Router).url).toBe('/login');
    /** Establish component-only sessions through the actual AuthService response flow. */
    function login(role: string): void { auth.login({ username: 'synthetic', password: 'synthetic-password' }).subscribe(); http.expectOne('/api/login').flush({ accessToken: `h.${btoa(JSON.stringify({ role }))}.s`, tokenType: 'Bearer', expiresAt: new Date(Date.now() + 900000).toISOString() }); }
    login('User'); await harness.navigateByUrl('/users/' + account().id + '/edit'); expect(TestBed.inject(Router).url).toBe('/login');
    login('Admin'); await harness.navigateByUrl('/users/' + account().id + '/edit', UserFormComponent); http.expectOne(url).flush(account()); harness.detectChanges(); expect(harness.routeNativeElement!.querySelector('h1')!.textContent).toBe('Edit account'); auth.clear(); http.verify();
  });
});
