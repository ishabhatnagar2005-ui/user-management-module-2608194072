/** Explain unavailable recovery without accepting data or triggering side effects. */
import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
@Component({ selector: 'app-forgot-password', standalone: true, imports: [RouterLink], styles: [':host { display: block; }'], template: `
  <section class="auth-page" aria-labelledby="recovery-title">
    <p class="eyebrow">Account access</p><h1 id="recovery-title">Password recovery</h1>
    <p>Self-service recovery is not available. Contact your organization’s account administrator through your established support channel.</p>
    <p class="muted">This page does not send email, verify identity or change your password.</p>
    <a class="button secondary" routerLink="/login">Back to sign in</a>
  </section>` })
export class ForgotPasswordComponent {}
