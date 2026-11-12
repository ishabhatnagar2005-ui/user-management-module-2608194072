/** Provide a concise public entry and truthful local sign-in confirmation. */
import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
@Component({ selector: 'app-home', standalone: true, imports: [RouterLink], styles: [':host { display: block; }'], template: `
  <section class="home-page" aria-labelledby="home-title">
    <p class="eyebrow">Organization accounts</p><h1 id="home-title">User management</h1>
    <p class="lead">A controlled workspace for account access and administration.</p>
    @if (auth.signedIn()) {
      <p role="status">You are signed in.</p><p class="muted">Use the navigation to access the capabilities available to your account.</p>
    } @else {
      <p class="muted">Sign in with the account provided by your administrator.</p><a class="button primary" routerLink="/login">Sign in</a>
    }
  </section>` })
export class HomeComponent { constructor(readonly auth: AuthService) {} }
