/** Present separate native profile and password forms without render-time mutation. */
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { passwordByteValidator } from '../auth/login.component';
import { NotificationService } from '../core/notification.service';
import { UserView } from '../core/api.models';
import { ProfileApiService, profileError } from './profile-api.service';
/** Reuse the inherited form rhythm and semantic feedback. */
@Component({ selector: 'app-profile', standalone: true, imports: [ReactiveFormsModule], templateUrl: './profile.component.html',
  styles: [':host { display: block; } .profile-page { max-width: 640px; margin: var(--space-8) auto; } .password-section { margin-top: var(--space-16); }'] })
export class ProfileComponent {
  private readonly destroyRef = inject(DestroyRef);
  readonly user = signal<UserView | null>(null);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly changingPassword = signal(false);
  readonly loadError = signal('');
  readonly error = signal('');
  readonly passwordError = signal('');
  readonly success = signal('');
  readonly fields = [
    { name: 'address', label: 'Address (optional)', maximum: 500, autocomplete: 'street-address' },
    { name: 'city', label: 'City (optional)', maximum: 100, autocomplete: 'address-level2' },
    { name: 'country', label: 'Country (optional)', maximum: 100, autocomplete: 'country-name' },
    { name: 'education', label: 'Education (optional)', maximum: 200, autocomplete: 'off' }
  ];
  readonly passwordFields = [{ name: 'currentPassword', label: 'Current password', autocomplete: 'current-password' },
    { name: 'newPassword', label: 'New password', autocomplete: 'new-password' }];
  readonly form = new FormGroup({ address: this.optional(500), city: this.optional(100), country: this.optional(100), education: this.optional(200) });
  readonly passwordForm = new FormGroup({
    currentPassword: new FormControl('', { nonNullable: true, validators: [Validators.required, passwordByteValidator] }),
    newPassword: new FormControl('', { nonNullable: true, validators: [Validators.required, passwordByteValidator] })
  });
  /** Fetch only safe profile data; mutations require explicit native submission. */
  constructor(private readonly api: ProfileApiService, private readonly auth: AuthService, private readonly notifications: NotificationService) { this.load(); }
  /** Build bounded optional text controls with the server's control-character policy. */
  private optional(maximum: number): FormControl<string> { return new FormControl('', { nonNullable: true, validators: [Validators.maxLength(maximum), Validators.pattern(/^[^\x00-\x1f\x7f-\x9f]*$/)] }); }
  /** Load or retry the safe account, preventing edits until the read succeeds. */
  load(): void {
    if (this.loading() || this.saving() || this.changingPassword()) return;
    this.loading.set(true); this.loadError.set(''); this.user.set(null);
    this.api.read().pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.loading.set(false))).subscribe({
      next: user => { this.user.set(user); this.form.reset({ address: user.address ?? '', city: user.city ?? '', country: user.country ?? '', education: user.education ?? '' }); },
      error: (error: unknown) => this.loadError.set(profileError(error))
    });
  }
  /** Associate errors with touched controls rather than modifying inputs during rendering. */
  invalid(field: string, password = false): boolean { const control = password ? this.passwordForm.get(field) : this.form.get(field); return !!control && control.invalid && control.touched; }
  /** Save one four-field request and announce success while preserving account identity. */
  save(): void {
    if (!this.user() || this.loading() || this.saving() || this.changingPassword()) return;
    this.form.markAllAsTouched(); this.error.set(''); this.success.set('');
    if (this.form.invalid) return;
    this.saving.set(true);
    this.api.update(this.form.getRawValue()).pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.saving.set(false))).subscribe({
      next: user => { this.user.set(user); this.form.markAsPristine(); this.success.set('Profile updated successfully.'); this.notifications.show(this.success()); },
      error: (error: unknown) => this.error.set(profileError(error))
    });
  }
  /** Verify and replace credentials once, erase both secrets, and clear the memory session on success. */
  changePassword(): void {
    if (!this.user() || this.loading() || this.saving() || this.changingPassword()) return;
    this.passwordForm.markAllAsTouched(); this.passwordError.set('');
    if (this.passwordForm.invalid) return;
    this.changingPassword.set(true);
    this.api.changePassword(this.passwordForm.getRawValue()).pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.changingPassword.set(false))).subscribe({
      next: () => { this.passwordForm.reset(); this.form.reset(); this.user.set(null); this.auth.passwordChanged(); },
      error: (error: unknown) => { this.passwordForm.reset(); this.passwordError.set(profileError(error, true)); }
    });
  }
}
