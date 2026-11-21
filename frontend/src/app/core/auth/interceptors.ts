import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { NotificationService } from '../ui/notification.service';
import { AuthService } from './auth.service';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const token = inject(AuthService).token();
  return next(token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req);
};

/** Surfaces API problem details as toasts (aborted / superseded requests are silent). */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const notifications = inject(NotificationService);
  const auth = inject(AuthService);
  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status !== 0 && err.status !== 499) {
        if (err.status === 401 && auth.enabled()) {
          void auth.login();
        } else if (err.status !== 404) {
          const p = err.error && typeof err.error === 'object' ? err.error : null;
          const detail = p?.errors ? Object.values(p.errors as Record<string, string[]>).flat().join(' ') : p?.detail;
          notifications.error(p?.title ?? `Request failed (${err.status})`, detail ?? err.message);
        }
      } else if (err instanceof HttpErrorResponse && err.status === 0) {
        notifications.error('API unreachable', 'Check that the Smart Meter API is running.');
      }
      return throwError(() => err);
    }),
  );
};
