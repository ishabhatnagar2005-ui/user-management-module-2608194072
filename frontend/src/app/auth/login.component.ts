/** Present an explicit accessible credential form without performing writes on render. */
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormControl, FormGroup, Validators, AbstractControl, ValidationErrors } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { finalize } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AuthService } from './auth.service';
/** Validate actual UTF-8 bounds rather than JavaScript character count. */
export function passwordByteValidator(control: AbstractControl): ValidationErrors | null {
  const size = new TextEncoder().encode(String(control.value ?? '')).length;
  return size >= 12 && size <= 72 ? null : { byteLength: true };
}
@Component({ selector: 'app-login', standalone: true, imports: [ReactiveFormsModule, RouterLink], styles: [':host { display: block; }'], template: `
  <section class="auth-page" aria-labelledby="login-title">
    <p class="eyebrow">Account access</p>
    <h1 id="login-title">Sign in</h1>
    <p class="muted">Use your organization-issued account. Your session stays in this tab.</p>
    <form [formGroup]="form" (ngSubmit)="submit()" novalidate [attr.aria-busy]="busy()">
      <div class="field">
        <label for="username">Username</label>
        <input id="username" formControlName="username" autocomplete="username" aria-required="true" spellcheck="false"
          [attr.aria-invalid]="invalid('username')" [attr.aria-describedby]="invalid('username') ? 'username-error' : null">
        @if (invalid('username')) { <p id="username-error" class="field-error" role="alert">Use 3–64 letters, digits, dots, hyphens or underscores.</p> }
      </div>
      <div class="field">
        <label for="password">Password</label>
        <input id="password" type="password" formControlName="password" autocomplete="current-password" aria-required="true"
          [attr.aria-invalid]="invalid('password')" [attr.aria-describedby]="invalid('password') ? 'password-error' : 'password-help'">
        <p id="password-help" class="help">12–72 UTF-8 bytes. You can paste your password.</p>
        @if (invalid('password')) { <p id="password-error" class="field-error" role="alert">Enter a password within the required byte range.</p> }
      </div>
      @if (error()) { <p class="alert-error" role="alert">{{ error() }}</p> }
      <button class="button primary" type="submit" [disabled]="busy()">{{ busy() ? 'Signing in…' : 'Sign in' }}</button>
      <p class="status" role="status">{{ busy() ? 'Verifying account credentials…' : '' }}</p>
    </form>
    <a routerLink="/forgot-password">Forgot password?</a>
    <p class="help">Accounts are created by an administrator. Public registration is not available.</p>
  </section>` })
export class LoginComponent {
  private readonly destroyRef = inject(DestroyRef);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly form = new FormGroup({ username: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.pattern(/^[A-Za-z0-9_.-]{3,64}$/)] }), password: new FormControl('', { nonNullable: true, validators: [Validators.required, passwordByteValidator] }) });
  constructor(private readonly auth: AuthService, private readonly router: Router) {}
  /** Expose field errors after interaction or attempted submission. */
  invalid(field: 'username' | 'password'): boolean { const control = this.form.controls[field]; return control.invalid && control.touched; }
  /** Block invalid/double submission and show safe retryable errors without redirecting failed login. */
  submit(): void {
    if (this.busy()) return;
    this.form.markAllAsTouched(); this.error.set('');
    if (this.form.invalid) return;
    this.busy.set(true);
    this.auth.login(this.form.getRawValue()).pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.busy.set(false))).subscribe({
      next: () => { this.form.controls.password.reset(); void this.router.navigate(['/']); },
      error: (error: unknown) => { this.form.controls.password.reset(); this.error.set(error instanceof HttpErrorResponse && error.status === 401 ? 'Credentials could not be verified. Check your username and password.' : error instanceof HttpErrorResponse && error.status === 400 ? 'Check the credential format and try again.' : 'Sign-in is temporarily unavailable. Please try again.'); }
    });
  }
}
