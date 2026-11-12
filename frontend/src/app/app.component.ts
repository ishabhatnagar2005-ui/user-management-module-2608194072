/** Compose the restrained responsive shell and shared live feedback. */
import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from './auth/auth.service';
import { NotificationService } from './core/notification.service';
@Component({ selector: 'app-root', standalone: true, imports: [RouterLink, RouterLinkActive, RouterOutlet], templateUrl: './app.component.html', styles: [':host { display: block; min-height: 100dvh; }'] })
export class AppComponent { constructor(readonly auth: AuthService, readonly notifications: NotificationService) {} }
