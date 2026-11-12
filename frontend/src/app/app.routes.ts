/** Keep one routing authority and reference only implemented slice components. */
import { Routes } from '@angular/router';
export const routes: Routes = [
  { path: '', title: 'User management', loadComponent: () => import('./home/home.component').then(m => m.HomeComponent) },
  { path: 'login', title: 'Sign in · User management', loadComponent: () => import('./auth/login.component').then(m => m.LoginComponent) },
  { path: 'forgot-password', title: 'Password recovery · User management', loadComponent: () => import('./auth/forgot-password.component').then(m => m.ForgotPasswordComponent) },
  { path: '**', redirectTo: '' }
];
