/** Attach bearer credentials only to the configured API and handle protected-session denial. */
import { inject } from '@angular/core';
import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
import { AuthService } from './auth.service';
import { environment } from '../../environments/environment';
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const isApi = request.url.startsWith(`${environment.apiBaseUrl}/`);
  const publicEndpoint = request.url.split('?')[0] === `${environment.apiBaseUrl}/login` || request.url.split('?')[0] === `${environment.apiBaseUrl}/health`;
  const token = isApi && !publicEndpoint ? auth.token() : null;
  const outgoing = token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;
  return next(outgoing).pipe(catchError((error: unknown) => {
    if (isApi && !publicEndpoint && error instanceof HttpErrorResponse && error.status === 401) auth.expire();
    return throwError(() => error);
  }));
};
