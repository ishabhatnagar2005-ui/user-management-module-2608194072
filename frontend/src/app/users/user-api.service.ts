/** Transport only typed safe account contracts through the configured API. */
import { Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { CreateUserRequest, UpdateUserRequest, UserView, DirectoryPage, DirectoryQuery } from '../core/api.models';
/** Delegate authorization to the existing interceptor and authoritative backend. */
@Injectable({ providedIn: 'root' })
export class UserApiService {
  /** Use Angular's shared client rather than maintaining a separate session. */
  constructor(private readonly http: HttpClient) {}
  /** Create an account only when an explicit form submission calls this method. */
  create(request: CreateUserRequest): Observable<UserView> { return this.http.post<UserView>(`${environment.apiBaseUrl}/users`, request); }
  /** Apply an explicit allowlisted patch through the authoritative transaction. */
  update(id: string, request: UpdateUserRequest): Observable<UserView> { return this.http.put<UserView>(`${environment.apiBaseUrl}/users/${encodeURIComponent(id)}`, request); }
  /** Soft-deactivate only after the detail confirmation action. */
  deactivate(id: string): Observable<UserView> { return this.http.delete<UserView>(`${environment.apiBaseUrl}/users/${encodeURIComponent(id)}`); }
  /** Serialize each approved directory value once, omitting empty optional filters. */
  list(query: DirectoryQuery): Observable<DirectoryPage> {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(query)) if (value !== '') params = params.set(key, String(value));
    return this.http.get<DirectoryPage>(`${environment.apiBaseUrl}/users`, { params });
  }
  /** Fetch one safe record; encode route data so it cannot introduce extra path segments. */
  read(id: string): Observable<UserView> { return this.http.get<UserView>(`${environment.apiBaseUrl}/users/${encodeURIComponent(id)}`); }
}
/** Translate transport failures into safe retry guidance without echoing server payloads. */
export function accountError(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    switch (error.status) {
      case 400: return 'Check the account fields and try again.';
      case 401: return 'Your session ended. Sign in again.';
      case 403: return 'Only administrators can access accounts.';
      case 404: return 'Account was not found.';
      case 409: return 'Username is already in use. Choose another username.';
    }
  }
  return 'Account service is temporarily unavailable. Please try again.';
}
