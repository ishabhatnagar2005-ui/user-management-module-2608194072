/** Own memory-only sessions; the API remains the authority for identity and authorization. */
import { Injectable, signal, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { CanActivateFn, Router } from '@angular/router';
import { Observable, tap } from 'rxjs';
import { environment } from '../../environments/environment';
import { LoginRequest, LoginResult, Role } from '../core/api.models';
import { NotificationService } from '../core/notification.service';
@Injectable({ providedIn: 'root' })
export class AuthService {
  private accessToken: string | null = null;
  private expiryTimer: ReturnType<typeof setTimeout> | null = null;
  private expiresAt = 0;
  readonly signedIn = signal(false);
  readonly role = signal<Role | null>(null);
  constructor(private readonly http: HttpClient, private readonly router: Router, private readonly notifications: NotificationService) {}
  /** Submit only explicit credentials and replace the previous account session. */
  login(request: LoginRequest): Observable<LoginResult> {
    this.clear();
    return this.http.post<LoginResult>(`${environment.apiBaseUrl}/login`, request).pipe(tap(result => {
      const expiry = Date.parse(result.expiresAt);
      if (result.tokenType !== 'Bearer' || !result.accessToken || !Number.isFinite(expiry) || expiry <= Date.now()) throw new Error('Invalid session response');
      let role: Role | null = null;
      try { const payload = JSON.parse(atob(result.accessToken.split('.')[1].replace(/-/g, '+').replace(/_/g, '/'))) as { role?: unknown }; role = payload.role === 'Admin' || payload.role === 'User' ? payload.role : null; }
      catch { throw new Error('Invalid session response'); }
      this.accessToken = result.accessToken;
      this.expiresAt = expiry;
      this.role.set(role);
      this.signedIn.set(true);
      this.expiryTimer = setTimeout(() => this.expire(), Math.min(expiry - Date.now(), 2147483647));
    }));
  }
  /** Return only a current token; never persist it in browser storage. */
  token(): string | null { if (this.accessToken && Date.now() >= this.expiresAt) this.expire(); return this.accessToken; }
  /** Clear every account-specific session value and notification. */
  clear(): void {
    if (this.expiryTimer !== null) clearTimeout(this.expiryTimer);
    this.expiryTimer = null; this.accessToken = null; this.expiresAt = 0;
    this.signedIn.set(false); this.role.set(null); this.notifications.clear();
  }
  /** End the local session without implying server-side token revocation. */
  logout(): void { this.clear(); this.notifications.show('You have signed out.'); void this.router.navigate(['/login']); }
  /** End all local account state after the server invalidates existing credentials. */
  passwordChanged(): void { this.clear(); this.notifications.show('Password changed successfully. Sign in with your new password.'); void this.router.navigate(['/login']); }
  /** Clear an expired/rejected session and announce why sign-in is needed. */
  expire(): void { this.clear(); this.notifications.show('Your session ended. Sign in again.'); void this.router.navigate(['/login']); }
}
/** Redirect protected pages; client guards are not an API security boundary. */
export const authGuard: CanActivateFn = () => { const auth = inject(AuthService); return auth.token() ? true : inject(Router).createUrlTree(['/login']); };
/** Hide administrator navigation from ordinary users; the API independently enforces roles. */
export const adminGuard: CanActivateFn = () => { const auth = inject(AuthService); return auth.token() && auth.role() === 'Admin' ? true : inject(Router).createUrlTree(['/login']); };
