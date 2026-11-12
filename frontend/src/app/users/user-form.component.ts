/** Provide an explicit, accessible create workflow using the inherited minimalist controls. */
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { passwordByteValidator } from '../auth/login.component';
import { NotificationService } from '../core/notification.service';
import { UserApiService, accountError } from './user-api.service';
/** Render creation only; no edit or deactivation behavior belongs to this slice. */
@Component({ selector: 'app-user-form', standalone: true, imports: [ReactiveFormsModule, RouterLink],
  styles: [':host { display: block; } .account-page { max-width: 640px; margin: var(--space-8) auto; }'], template: `
  <section class="account-page" aria-labelledby="account-title">
    <p class="eyebrow">Administrator workspace</p>
    <h1 id="account-title">New account</h1>
    <p class="muted">Create an organization account. New accounts receive the User role.</p>
    <form [formGroup]="form" (ngSubmit)="submit()" novalidate [attr.aria-busy]="busy()">
      @for (field of fields; track field.name) {
        <div class="field">
          <label [for]="field.name">{{ field.label }}</label>
          <input [id]="field.name" [formControlName]="field.name" [type]="field.name === 'password' ? 'password' : 'text'"
            [attr.autocomplete]="field.name === 'password' ? 'new-password' : 'off'" [attr.aria-required]="field.required"
            [attr.aria-invalid]="invalid(field.name)" [attr.aria-describedby]="invalid(field.name) ? field.name + '-error' : field.name + '-help'">
          <p [id]="field.name + '-help'" class="help">{{ field.help }}</p>
          @if (invalid(field.name)) { <p [id]="field.name + '-error'" class="field-error" role="alert">{{ field.help }}</p> }
        </div>
      }
      @if (error()) { <p class="alert-error" role="alert">{{ error() }}</p> }
      <button class="button primary" type="submit" [disabled]="busy()">{{ busy() ? 'Creating account…' : 'Create account' }}</button>
      <p class="status" role="status">{{ busy() ? 'Saving account securely…' : '' }}</p>
    </form>
    <a routerLink="/">Cancel and return home</a>
  </section>` })
export class UserFormComponent {
  private readonly destroyRef = inject(DestroyRef);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly fields = [
    { name: 'username', label: 'Username', required: true, help: 'Use 3–64 letters, digits, dots, hyphens or underscores. Case matters.' },
    { name: 'password', label: 'Password', required: true, help: 'Use 12–72 UTF-8 bytes. You can paste your password.' },
    { name: 'address', label: 'Address (optional)', required: false, help: 'Up to 500 characters, without control characters.' },
    { name: 'city', label: 'City (optional)', required: false, help: 'Up to 100 characters, without control characters.' },
    { name: 'country', label: 'Country (optional)', required: false, help: 'Up to 100 characters, without control characters.' },
    { name: 'education', label: 'Education (optional)', required: false, help: 'Up to 200 characters, without control characters.' },
    { name: 'aadhaar_no', label: 'Aadhaar identifier (optional)', required: false, help: 'Synthetic development identifiers only. Up to 64 characters; encrypted and never displayed.' },
    { name: 'pan_no', label: 'PAN identifier (optional)', required: false, help: 'Synthetic development identifiers only. Up to 64 characters; encrypted and never displayed.' }
  ];
  readonly form = new FormGroup({
    username: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.pattern(/^[A-Za-z0-9_.-]{3,64}$/)] }),
    password: new FormControl('', { nonNullable: true, validators: [Validators.required, passwordByteValidator] }),
    address: this.optional(500), city: this.optional(100), country: this.optional(100), education: this.optional(200), aadhaar_no: this.optional(64), pan_no: this.optional(64)
  });
  /** Inject real transport and the existing shared success announcement. */
  constructor(private readonly api: UserApiService, private readonly router: Router, private readonly notifications: NotificationService) {}
  /** Build bounded optional native controls with the same control-character policy as the API. */
  private optional(maximum: number): FormControl<string> { return new FormControl('', { nonNullable: true, validators: [Validators.maxLength(maximum), Validators.pattern(/^[^\x00-\x1f\x7f-\x9f]*$/)] }); }
  /** Associate field errors after blur or an explicit attempted submission. */
  invalid(field: string): boolean { const control = this.form.get(field); return !!control && control.invalid && control.touched; }
  /** Block invalid/double writes, clear secrets, and navigate to the real created detail. */
  submit(): void {
    if (this.busy()) return;
    this.form.markAllAsTouched(); this.error.set('');
    if (this.form.invalid) return;
    this.busy.set(true);
    this.api.create(this.form.getRawValue()).pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.busy.set(false))).subscribe({
      next: user => { this.form.reset(); this.notifications.show('Account created successfully.'); void this.router.navigate(['/users', user.id]); },
      error: (error: unknown) => { this.form.controls.password.reset(); this.form.controls.aadhaar_no.reset(); this.form.controls.pan_no.reset(); this.error.set(accountError(error)); }
    });
  }
}
