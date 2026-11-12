/** Verify directory controls, safe transport, URL state and visible asynchronous branches. */
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { DirectoryComponent } from './directory.component';
import { DirectoryPage } from '../core/api.models';
/** Supply safe component-only responses while backend and E2E tests retain real persistence. */
function page(limit = 25, skip = 0): DirectoryPage {
  return { limit, skip, items: [{ id: '000000000000000000000001', username: 'directory.alpha', city: 'Synthetic city', country: 'A', education: 'A', is_active: true, created_at: new Date().toISOString(), updated_at: new Date().toISOString() }] };
}
/** Exercise native controls and observable route changes at Angular's HTTP test boundary. */
describe('Directory controls and URL state', () => {
  let fixture: ComponentFixture<DirectoryComponent>;
  let http: HttpTestingController;
  let params: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  let navigate: jasmine.Spy;
  /** Create the native reactive form with controllable browser-history query emissions. */
  beforeEach(() => {
    params = new BehaviorSubject(convertToParamMap({}));
    TestBed.configureTestingModule({ imports: [DirectoryComponent], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      { provide: ActivatedRoute, useValue: { queryParamMap: params, snapshot: { queryParamMap: convertToParamMap({}) } } }] });
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    http = TestBed.inject(HttpTestingController); fixture = TestBed.createComponent(DirectoryComponent); fixture.detectChanges();
  });
  /** Fail on unexpected requests and destroy subscriptions after each scenario. */
  afterEach(() => { fixture.destroy(); http.verify(); });
  /** Select a pending collection read independently of query serialization order. */
  function pending() { return http.expectOne(r => r.url === '/api/users' && r.method === 'GET'); }
  /** Enter data through a labelled native input or select. */
  function enter(id: string, value: string): void {
    const input = fixture.nativeElement.querySelector(`#${id}`) as HTMLInputElement;
    input.value = value; input.dispatchEvent(new Event(input.tagName === 'SELECT' ? 'change' : 'input')); input.dispatchEvent(new Event('blur')); fixture.detectChanges();
  }
  /** Submit the actual rendered reactive form. */
  function submit(): void { fixture.nativeElement.querySelector('form').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); fixture.detectChanges(); }
  /** Find an explicit action by its visible button text. */
  function button(name: string): HTMLButtonElement { return Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>).find(b => b.textContent?.trim() === name)!; }
  it('shows default loading then accessible safe results and real create/detail links', () => {
    expect(fixture.nativeElement.querySelector('[aria-busy]').getAttribute('aria-busy')).toBe('true'); expect(fixture.nativeElement.textContent).toContain('Loading directory');
    const request = pending(); expect(request.request.params.get('limit')).toBe('25'); expect(request.request.params.get('skip')).toBe('0'); expect(request.request.params.has('search')).toBeFalse();
    request.flush(page()); fixture.detectChanges(); expect(fixture.nativeElement.querySelector('table')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('th[aria-sort=ascending]').textContent).toContain('Username'); expect(fixture.nativeElement.textContent).toContain('Synthetic city');
    expect(fixture.nativeElement.querySelector('a[href="/users/new"]')).not.toBeNull(); expect(fixture.nativeElement.querySelector('a[href="/users/000000000000000000000001"]')).not.toBeNull(); http.expectNone(r => r.method !== 'GET');
  });
  it('serializes every control to URL state and resets offset when applying a query', () => {
    pending().flush(page()); enter('search', 'Synthetic.*city'); enter('country', 'A'); enter('education', 'A'); enter('status', 'inactive'); enter('sortBy', 'education'); enter('sortDirection', 'desc'); enter('limit', '1'); submit();
    expect(navigate.calls.mostRecent().args[1].queryParams).toEqual({ search: 'Synthetic.*city', country: 'A', education: 'A', status: 'inactive', sortBy: 'education', sortDirection: 'desc', limit: 1, skip: 0 });
    params.next(convertToParamMap(navigate.calls.mostRecent().args[1].queryParams)); const request = pending(); expect(request.request.params.get('search')).toBe('Synthetic.*city'); request.flush(page(1)); fixture.detectChanges(); expect(fixture.nativeElement.querySelector('th[aria-sort=descending]').textContent).toContain('Education');
  });
  it('restores Back/Forward query state and cancels superseded reads', () => {
    const initial = pending(); params.next(convertToParamMap({ search: 'next', limit: '1', skip: '1', country: 'Z', education: 'Z', status: 'active', sortBy: 'city', sortDirection: 'desc' })); expect(initial.cancelled).toBeTrue();
    const next = pending(); expect(next.request.params.get('skip')).toBe('1'); next.flush(page(1, 1)); fixture.detectChanges(); expect(fixture.nativeElement.querySelector('#search').value).toBe('next');
    params.next(convertToParamMap({})); pending().flush(page()); fixture.detectChanges(); expect(fixture.nativeElement.querySelector('#search').value).toBe(''); expect(fixture.nativeElement.querySelector('#limit').value).toBe('25');
  });
  it('keeps checkbox selection inert and bulk/export disabled with explicit reasons', () => {
    pending().flush(page()); fixture.detectChanges(); const checkbox = fixture.nativeElement.querySelector('input[type=checkbox]') as HTMLInputElement;
    checkbox.click(); fixture.detectChanges(); expect(fixture.nativeElement.textContent).toContain('1 rows selected');
    for (const name of ['Bulk actions', 'Export']) { expect(button(name).disabled).toBeTrue(); expect(fixture.nativeElement.querySelector('#' + button(name).getAttribute('aria-describedby'))).not.toBeNull(); button(name).click(); }
    checkbox.click(); fixture.detectChanges(); expect(fixture.nativeElement.textContent).toContain('0 rows selected'); http.expectNone(r => r.method !== 'GET');
  });
  it('navigates offset windows without a total-count assumption', () => {
    pending().flush(page()); params.next(convertToParamMap({ limit: '1', skip: '1' })); pending().flush(page(1, 1)); fixture.detectChanges();
    button('Next page').click(); expect(navigate.calls.mostRecent().args[1].queryParams.skip).toBe(2); button('Previous page').click(); expect(navigate.calls.mostRecent().args[1].queryParams.skip).toBe(0);
  });
  it('distinguishes unfiltered no-data from filtered-empty and disables exhausted paging', () => {
    pending().flush({ items: [], limit: 25, skip: 0 }); fixture.detectChanges(); expect(fixture.nativeElement.textContent).toContain('No accounts yet'); expect(fixture.nativeElement.querySelector('table')).toBeNull();
    params.next(convertToParamMap({ search: 'missing' })); pending().flush({ items: [], limit: 25, skip: 0 }); fixture.detectChanges(); expect(fixture.nativeElement.textContent).toContain('No accounts match'); expect(button('Next page').disabled).toBeTrue();
  });
  for (const status of [0, 400, 401, 403, 503]) {
    it(`renders safe ${status} failure and retries without dropping query state`, () => {
      const first = pending(); if (status === 0) first.error(new ProgressEvent('network')); else first.flush({ title: 'secret-value' }, { status, statusText: 'Failure' }); fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('[role=alert]').textContent).not.toContain('secret-value'); expect(fixture.nativeElement.querySelector('table')).toBeNull(); button('Retry directory').click(); pending().flush(page()); fixture.detectChanges(); expect(fixture.nativeElement.querySelector('[role=alert]')).toBeNull();
    });
  }
  for (const invalid of [{ limit: '0' }, { limit: '101' }, { limit: '1.5' }, { skip: '-1' }, { search: '{"$ne":null}' }, { sortBy: 'password' }, { sortDirection: 'up' }, { status: 'all' }, { unknown: 'x' }, { search: ['a', 'b'] }]) {
    it(`rejects malformed URL ${JSON.stringify(invalid)} without silently ignoring it`, () => {
      pending().flush(page()); params.next(convertToParamMap(invalid)); fixture.detectChanges(); http.expectNone(r => r.url === '/api/users'); expect(fixture.nativeElement.querySelector('[role=alert]').textContent).toContain('invalid query');
    });
  }
  it('blocks out-of-bounds form values with accessible page-size guidance', () => {
    pending().flush(page()); enter('limit', '101'); submit(); expect(navigate).not.toHaveBeenCalled(); expect(fixture.nativeElement.querySelector('#limit').getAttribute('aria-invalid')).toBe('true'); expect(fixture.nativeElement.querySelector('[role=alert]').textContent).toContain('whole page size');
  });
  it('resets URL and refreshes the page', fakeAsync(() => {
    pending().flush(page()); button('Reset filters').click(); expect(navigate.calls.mostRecent().args[1].queryParams).toEqual({}); tick(); pending().flush(page());
  }));
  it('cancels pending reads on destroy', () => { const request = pending(); fixture.destroy(); expect(request.cancelled).toBeTrue(); });
  it('reports failed router navigation instead of silently losing query state', fakeAsync(() => {
    pending().flush(page()); navigate.and.rejectWith(new Error('secret')); enter('search', 'next'); submit(); tick(); fixture.detectChanges(); expect(fixture.nativeElement.querySelector('[role=alert]').textContent).toContain('navigation failed');
  }));
});
