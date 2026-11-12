/** Read and render one safe account without mutation on component initialization. */
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { UserView } from '../core/api.models';
import { UserApiService, accountError } from './user-api.service';
/** Keep account detail read-only, including its loading and safe error states. */
@Component({ selector: 'app-user-detail', standalone: true, imports: [DatePipe, RouterLink],
  styles: [':host { display: block; } section { max-width: 640px; margin: var(--space-8) auto; } dt { font-weight: 600; } dd { margin: 0 0 var(--space-6); overflow-wrap: anywhere; }'], template: `
  <section aria-labelledby="detail-title" [attr.aria-busy]="busy()">
    <p class="eyebrow">Administrator workspace</p>
    <h1 id="detail-title">Account details</h1>
    @if (busy()) { <p class="status" role="status">Loading account…</p> }
    @if (error()) { <p class="alert-error" role="alert">{{ error() }}</p> }
    @if (user(); as account) {
      <dl>
        <dt>Username</dt><dd>{{ account.username }}</dd>
        <dt>Account ID</dt><dd>{{ account.id }}</dd>
        <dt>Status</dt><dd>{{ account.is_active ? 'Active' : 'Inactive' }}</dd>
        <dt>Address</dt><dd>{{ account.address || 'Not provided' }}</dd>
        <dt>City</dt><dd>{{ account.city || 'Not provided' }}</dd>
        <dt>Country</dt><dd>{{ account.country || 'Not provided' }}</dd>
        <dt>Education</dt><dd>{{ account.education || 'Not provided' }}</dd>
        <dt>Created</dt><dd>{{ account.created_at | date:'medium':'UTC' }} UTC</dd>
        <dt>Updated</dt><dd>{{ account.updated_at | date:'medium':'UTC' }} UTC</dd>
      </dl>
      <p class="help">Passwords and protected identifiers are never displayed.</p>
    }
    <a routerLink="/users/new">New account</a>
    <p><a routerLink="/">Return home</a></p>
  </section>` })
export class UserDetailComponent {
  readonly busy = signal(true);
  readonly error = signal('');
  readonly user = signal<UserView | null>(null);
  /** Observe route identity changes and cancel superseded reads; never issue a write on render. */
  constructor(route: ActivatedRoute, api: UserApiService) {
    route.paramMap.pipe(
      tap(() => { this.busy.set(true); this.error.set(''); this.user.set(null); }),
      switchMap(params => api.read(params.get('id') ?? '').pipe(catchError((error: unknown) => { this.error.set(accountError(error)); return of(null); }))),
      takeUntilDestroyed(inject(DestroyRef))
    ).subscribe(user => { this.user.set(user); this.busy.set(false); });
  }
}
