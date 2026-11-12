/** Bootstrap Angular's router and HttpClient interceptor exactly once. */
import { bootstrapApplication } from '@angular/platform-browser';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withInMemoryScrolling } from '@angular/router';
import { AppComponent } from './app/app.component';
import { routes } from './app/app.routes';
import { authInterceptor } from './app/auth/auth.interceptor';
bootstrapApplication(AppComponent, { providers: [provideHttpClient(withInterceptors([authInterceptor])), provideRouter(routes, withInMemoryScrolling({ scrollPositionRestoration: 'enabled' }))] }).catch(() => {
  const root = document.querySelector('app-root'); if (root) root.textContent = 'The account workspace could not start. Please reload or contact your administrator.';
});
