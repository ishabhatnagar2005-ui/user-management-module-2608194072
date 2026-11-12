/** Read safe administrator activity using the configured Angular bearer transport. */
import { Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuditPage, DashboardView } from '../core/api.models';
/** Keep activity transport read-only with no hidden query controls or cache. */
@Injectable({ providedIn: 'root' })
export class ActivityApiService {
  /** Reuse the shared client and its registered authorization interceptor. */
  constructor(private readonly http: HttpClient) {}
  /** Fetch the latest hundred approved redacted audit outcomes. */
  logs(): Observable<AuditPage> { return this.http.get<AuditPage>(`${environment.apiBaseUrl}/logs`); }
  /** Fetch six summaries from the server's consistent source snapshot. */
  dashboard(): Observable<DashboardView> { return this.http.get<DashboardView>(`${environment.apiBaseUrl}/dashboard`); }
}
/** Map failures to fixed safe guidance without echoing arbitrary server content. */
export function activityError(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 401) return 'Your session ended. Sign in again.';
    if (error.status === 403) return 'Administrator access is required.';
  }
  return 'Activity service is temporarily unavailable. Please try again.';
}
