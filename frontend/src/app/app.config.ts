import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding, withInMemoryScrolling } from '@angular/router';
import { routes } from './app.routes';
import { AppConfigService } from './core/config/app-config';
import { AuthService } from './core/auth/auth.service';
import { authInterceptor, errorInterceptor } from './core/auth/interceptors';
import { setLocale } from './core/ui/format';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding(), withInMemoryScrolling({ scrollPositionRestoration: 'top' })),
    provideHttpClient(withFetch(), withInterceptors([authInterceptor, errorInterceptor])),
    provideAppInitializer(async () => {
      const config = inject(AppConfigService);
      const auth = inject(AuthService);
      await config.load();
      setLocale(config.value.locale);
      await auth.init();
    }),
  ],
};
