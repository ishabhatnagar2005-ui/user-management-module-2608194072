/** Keep one routing authority and reference only implemented slice components. */
import { Routes } from '@angular/router';
import { adminGuard } from './auth/auth.service';
export const routes: Routes = [
  { path: '', title: 'User management', loadComponent: () => import('./home/home.component').then(m => m.HomeComponent) },
  { path: 'login', title: 'Sign in · User management', loadComponent: () => import('./auth/login.component').then(m => m.LoginComponent) },
  { path: 'forgot-password', title: 'Password recovery · User management', loadComponent: () => import('./auth/forgot-password.component').then(m => m.ForgotPasswordComponent) },
  { path: 'users/new', title: 'New account · User management', canActivate: [adminGuard], loadComponent: () => import('./users/user-form.component').then(m => m.UserFormComponent) },
  { path: 'users/:id', title: 'Account details · User management', canActivate: [adminGuard], loadComponent: () => import('./users/user-detail.component').then(m => m.UserDetailComponent) },
  { path: '**', redirectTo: '' }
];
