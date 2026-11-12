/** Transport principal-only profile operations with the existing bearer interceptor. */
import { Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { PasswordChangeRequest, ProfileUpdateRequest, UserView } from '../core/api.models';
/** Use the shared configured client without accepting a target identity. */
@Injectable({ providedIn: 'root' })
export class ProfileApiService {
  /** Inject the shared Angular transport. */
  constructor(private readonly http: HttpClient) {}
  /** Read the authenticated account without mutation. */
  read(): Observable<UserView> { return this.http.get<UserView>(`${environment.apiBaseUrl}/profile`); }
  /** Save only approved profile fields on explicit submission. */
  update(request: ProfileUpdateRequest): Observable<UserView> { return this.http.put<UserView>(`${environment.apiBaseUrl}/profile`, request); }
  /** Send current-password proof and a replacement without storing either secret. */
  changePassword(request: PasswordChangeRequest): Observable<void> { return this.http.post<void>(`${environment.apiBaseUrl}/profile/password`, request); }
}
/** Show safe guidance without echoing arbitrary response payloads or secrets. */
export function profileError(error: unknown, password = false): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 400) return password ? 'Check your current password and use 12–72 UTF-8 bytes for the new password.' : 'Check the profile fields and try again.';
    if (error.status === 401) return 'Your session ended. Sign in again.';
    if (error.status === 403) return 'Access is not permitted.';
    if (error.status === 404) return 'Account was not found.';
  }
  return 'Profile service is temporarily unavailable. Please try again.';
}
