/** Browse safe directory records with the URL as the single navigable query authority. */
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormControl, FormGroup, Validators, AbstractControl, ValidationErrors } from '@angular/forms';
import { ActivatedRoute, ParamMap, Router, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { catchError, combineLatest, of, startWith, Subject, switchMap, tap } from 'rxjs';
import { DirectoryPage, DirectoryQuery } from '../core/api.models';
import { UserApiService } from './user-api.service';
/** Reject operator-shaped/control text at the form boundary without treating regex punctuation as syntax. */
function literalValidator(control: AbstractControl): ValidationErrors | null {
  return /[\x00-\x1f\x7f-\x9f$\{\}\[\]]/.test(String(control.value ?? '')) ? { literal: true } : null;
}
/** Keep selection inert and expose loading, empty, validation and retry states. */
@Component({ selector: 'app-directory', standalone: true, imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './directory.component.html', styles: [`
    :host { display: block; } .heading, .actions { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-4); justify-content: space-between; }
    .filters { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 210px), 1fr)); gap: var(--space-4); }
    .field { margin-bottom: 0; } .table-wrap { overflow-x: auto; margin: var(--space-6) 0; } table { width: 100%; min-width: 680px; border-collapse: collapse; font-variant-numeric: tabular-nums; }
    caption { text-align: left; color: var(--foreground-muted); margin-bottom: var(--space-3); } th, td { text-align: left; padding: var(--space-3); border-bottom: 1px solid var(--border); }
    th { font-weight: 600; } td { overflow-wrap: anywhere; max-width: 240px; } input[type=checkbox] { width: 24px; min-height: 24px; accent-color: var(--accent); }
    .selection { display: flex; gap: var(--space-3); align-items: center; margin: 0; } .selection label { margin: 0; } button:disabled { cursor: not-allowed; }
  `] })
export class DirectoryComponent {
  readonly busy = signal(true);
  readonly error = signal('');
  readonly page = signal<DirectoryPage | null>(null);
  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly query = signal<DirectoryQuery>({ limit: 25, skip: 0, search: '', country: '', education: '', status: '', sortBy: 'username', sortDirection: 'asc' });
  readonly form = new FormGroup({
    search: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(200), literalValidator] }),
    country: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(100), literalValidator] }),
    education: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(200), literalValidator] }),
    status: new FormControl<DirectoryQuery['status']>('', { nonNullable: true }),
    sortBy: new FormControl<DirectoryQuery['sortBy']>('username', { nonNullable: true }),
    sortDirection: new FormControl<DirectoryQuery['sortDirection']>('asc', { nonNullable: true }),
    limit: new FormControl(25, { nonNullable: true, validators: [Validators.required, Validators.min(1), Validators.max(100), Validators.pattern(/^[0-9]+$/)] })
  });
  private readonly reload = new Subject<void>();
  private readonly route: ActivatedRoute;
  private readonly router: Router;
  /** Synchronize controls on refresh/Back/Forward and cancel superseded network requests. */
  constructor(route: ActivatedRoute, router: Router, api: UserApiService) {
    this.route = route; this.router = router;
    combineLatest([route.queryParamMap, this.reload.pipe(startWith(undefined))]).pipe(
      tap(([params]) => {
        const query = this.fromUrl(params); this.query.set(query); this.form.patchValue(query, { emitEvent: false });
        this.busy.set(true); this.error.set(''); this.page.set(null); this.selected.set(new Set());
      }),
      switchMap(([params]) => {
        if (!this.validUrl(params)) { this.error.set('The directory URL contains an invalid query. Reset filters to continue.'); return of(null); }
        return api.list(this.query()).pipe(catchError((failure: unknown) => {
          this.error.set(failure instanceof HttpErrorResponse && failure.status === 400 ? 'Check the directory query and try again.' :
            failure instanceof HttpErrorResponse && failure.status === 401 ? 'Your session ended. Sign in again.' :
            failure instanceof HttpErrorResponse && failure.status === 403 ? 'Only administrators can browse the directory.' :
            'Directory is temporarily unavailable. Please try again.');
          return of(null);
        }));
      }), takeUntilDestroyed(inject(DestroyRef))
    ).subscribe(page => { this.page.set(page); this.busy.set(false); });
  }
  /** Copy URL defaults without discarding invalid values before validation. */
  private fromUrl(params: ParamMap): DirectoryQuery {
    return { limit: Number(params.get('limit') ?? 25), skip: Number(params.get('skip') ?? 0),
      search: params.get('search') ?? '', country: params.get('country') ?? '', education: params.get('education') ?? '',
      status: (params.get('status') ?? '') as DirectoryQuery['status'], sortBy: (params.get('sortBy') ?? 'username') as DirectoryQuery['sortBy'],
      sortDirection: (params.get('sortDirection') ?? 'asc') as DirectoryQuery['sortDirection'] };
  }
  /** Reject unsupported/duplicate URL state locally instead of silently ignoring it. */
  private validUrl(params: ParamMap): boolean {
    const q = this.query();
    return params.keys.every(key => Object.keys(q).includes(key) && params.getAll(key).length === 1) &&
      ['limit', 'skip'].every(key => !params.has(key) || /^[0-9]+$/.test(params.get(key)!)) && Number.isSafeInteger(q.skip) && q.skip >= 0 && q.skip <= 2147483647 &&
      ['status', 'sortBy', 'sortDirection'].every(key => !params.has(key) || params.get(key) !== '') &&
      ['username', 'city', 'country', 'education'].includes(q.sortBy) && ['asc', 'desc'].includes(q.sortDirection) && ['', 'active', 'inactive'].includes(q.status) && this.form.valid &&
      [q.search, q.country, q.education].every(value => !/[\x00-\x1f\x7f-\x9f$\{\}\[\]]/.test(value));
  }
  /** Apply explicit reactive control submission and reset the offset for a changed query. */
  apply(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid) return;
    this.navigate({ ...this.form.getRawValue(), skip: 0 });
  }
  /** Persist navigation state so links, refresh and browser history share the same query. */
  private navigate(query: DirectoryQuery): void {
    const params = Object.fromEntries(Object.entries(query).filter(([, value]) => value !== ''));
    if (Object.entries(query).every(([key, value]) => value === this.query()[key as keyof DirectoryQuery]) && this.validUrl(this.route.snapshot.queryParamMap)) { this.retry(); return; }
    void this.router.navigate([], { relativeTo: this.route, queryParams: params }).catch(() => this.error.set('Directory navigation failed. Please try again.'));
  }
  /** Return to the default unfiltered first page using actual router history. */
  reset(): void {
    const alreadyDefault = this.route.snapshot.queryParamMap.keys.length === 0;
    void this.router.navigate([], { relativeTo: this.route, queryParams: {} }).then(() => { if (alreadyDefault) this.retry(); }).catch(() => this.error.set('Directory navigation failed. Please try again.'));
  }
  /** Retry the current URL rather than losing filters after a network failure. */
  retry(): void { this.reload.next(); }
  /** Move the approved offset window without asserting that a total count exists. */
  move(direction: -1 | 1): void { this.navigate({ ...this.query(), skip: Math.max(0, this.query().skip + direction * this.query().limit) }); }
  /** Report accessible ordering from the current URL, not unsaved form values. */
  sortState(field: DirectoryQuery['sortBy']): string { return this.query().sortBy === field ? (this.query().sortDirection === 'asc' ? 'ascending' : 'descending') : 'none'; }
  /** Change only in-memory selection; there is intentionally no mutation or export transport. */
  toggle(id: string, event: Event): void {
    const selected = new Set(this.selected());
    if ((event.target as HTMLInputElement).checked) selected.add(id); else selected.delete(id);
    this.selected.set(selected);
  }
}
