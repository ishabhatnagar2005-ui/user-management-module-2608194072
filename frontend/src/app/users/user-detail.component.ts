/** Read and render one safe account without mutation on component initialization. */
import { Component, DestroyRef, inject, signal, ElementRef, ViewChild } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap, finalize } from 'rxjs';
import { UserView } from '../core/api.models';
import { UserApiService, accountError } from './user-api.service';
/** Render safe detail and require a native confirmation before soft deactivation. */
@Component({ selector: 'app-user-detail', standalone: true, imports: [DatePipe, RouterLink],
  styles: [':host { display: block; } section { max-width: 640px; margin: var(--space-8) auto; } dt { font-weight: 600; } dd { margin: 0 0 var(--space-6); overflow-wrap: anywhere; } dialog { max-width: min(90vw, 480px); color: var(--foreground); background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); padding: var(--space-6); } dialog::backdrop { background: #0005; }'], template: `
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
      <p><a #editLink [routerLink]="['/users', account.id, 'edit']">Edit account</a></p>
      @if (account.is_active) { <button #trigger class="button secondary" type="button" [disabled]="mutating()" (click)="openDialog(trigger)">Deactivate account</button> }
      @if (success()) { <p role="status">{{ success() }}</p> }
      <p class="help">Passwords and protected identifiers are never displayed.</p>
    }
    <dialog #confirmation aria-modal="true" aria-labelledby="confirm-title" (cancel)="escape($event)" (close)="restoreFocus()" (click)="backdrop($event)">
      <h2 id="confirm-title">Deactivate this account?</h2>
      <p>The account will become inactive. Its record is retained. This cannot be undone here.</p>
      @if (mutationError()) { <p role="alert" class="alert-error">{{ mutationError() }}</p> }
      <p role="status">{{ mutating() ? 'Deactivating account…' : '' }}</p>
      <button type="button" class="button secondary" autofocus [disabled]="mutating()" (click)="closeDialog()">Cancel</button>
      <button type="button" class="button primary" [disabled]="mutating()" (click)="confirm()">{{ mutating() ? 'Deactivating…' : 'Confirm deactivation' }}</button>
    </dialog>
    <p><a routerLink="/users">Browse directory</a></p>
    <a routerLink="/users/new">New account</a>
    <p><a routerLink="/">Return home</a></p>
  </section>` })
export class UserDetailComponent {
  @ViewChild('editLink') private editLink?: ElementRef<HTMLAnchorElement>;
  @ViewChild('confirmation') private confirmation?: ElementRef<HTMLDialogElement>;
  private trigger?: HTMLElement;
  private previousOverflow = '';
  private readonly destroyRef = inject(DestroyRef);
  readonly mutating = signal(false);
  readonly mutationError = signal('');
  readonly success = signal('');
  readonly busy = signal(true);
  readonly error = signal('');
  readonly user = signal<UserView | null>(null);
  /** Observe route identity changes and cancel superseded reads; never issue a write on render. */
  constructor(route: ActivatedRoute, private readonly api: UserApiService) {
    this.destroyRef.onDestroy(() => { document.body.style.overflow = this.previousOverflow; });
    route.paramMap.pipe(
      tap(() => { this.closeDialog(); this.success.set(''); this.busy.set(true); this.error.set(''); this.user.set(null); }),
      switchMap(params => api.read(params.get('id') ?? '').pipe(catchError((error: unknown) => { this.error.set(accountError(error)); return of(null); }))),
      takeUntilDestroyed(this.destroyRef)
    ).subscribe(user => { this.user.set(user); this.busy.set(false); });
  }
  /** Open a native modal only on an explicit action; focus stays inside until close. */
  openDialog(trigger: HTMLElement): void {
    if (this.mutating() || !this.user()?.is_active) return;
    this.trigger = trigger; this.mutationError.set(''); this.previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden'; this.confirmation?.nativeElement.showModal();
  }
  /** Cancel without a request and restore the original scroll/focus state. */
  closeDialog(): void { if (this.mutating()) return; this.confirmation?.nativeElement.close(); this.restoreFocus(); }
  /** Let Escape cancel only before a transaction has been submitted. */
  escape(event: Event): void { event.preventDefault(); this.closeDialog(); }
  /** Dismiss a backdrop click without treating dialog content clicks as cancellation. */
  backdrop(event: MouseEvent): void { if (event.target === this.confirmation?.nativeElement) {
    const rect = this.confirmation.nativeElement.getBoundingClientRect();
    if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) this.closeDialog();
  } }
  /** Restore focus to the trigger, or the edit link when the inactive trigger is removed. */
  restoreFocus(): void { document.body.style.overflow = this.previousOverflow; if (this.user()?.is_active) this.trigger?.focus(); else this.editLink?.nativeElement.focus(); }
  /** Submit once after confirmation and retain visible data on failure for retry. */
  confirm(): void {
    const account = this.user();
    if (!this.confirmation?.nativeElement.open || !account?.is_active || this.mutating()) return;
    this.mutating.set(true); this.mutationError.set('');
    this.api.deactivate(account.id).pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.mutating.set(false))).subscribe({
      next: user => { this.user.set(user); this.success.set('Account deactivated successfully.'); this.confirmation?.nativeElement.close(); },
      error: (error: unknown) => this.mutationError.set(accountError(error))
    });
  }
}
