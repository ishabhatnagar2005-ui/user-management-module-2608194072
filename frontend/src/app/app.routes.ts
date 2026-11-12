/** Keep one routing authority and reference only implemented slice components. */
import { Routes } from '@angular/router';
import { adminGuard, authGuard } from './auth/auth.service';
export const routes: Routes = [
  { path: '', title: 'User management', loadComponent: () => import('./home/home.component').then(m => m.HomeComponent) },
  { path: 'login', title: 'Sign in · User management', loadComponent: () => import('./auth/login.component').then(m => m.LoginComponent) },
  { path: 'forgot-password', title: 'Password recovery · User management', loadComponent: () => import('./auth/forgot-password.component').then(m => m.ForgotPasswordComponent) },
  { path: 'users', title: 'Directory · User management', canActivate: [adminGuard], loadComponent: () => import('./users/directory.component').then(m => m.DirectoryComponent) },
  { path: 'users/new', title: 'New account · User management', canActivate: [adminGuard], loadComponent: () => import('./users/user-form.component').then(m => m.UserFormComponent) },
  { path: 'users/:id/edit', title: 'Edit account · User management', canActivate: [adminGuard], loadComponent: () => import('./users/user-form.component').then(m => m.UserFormComponent) },
  { path: 'users/:id', title: 'Account details · User management', canActivate: [adminGuard], loadComponent: () => import('./users/user-detail.component').then(m => m.UserDetailComponent) },
  { path: 'profile', title: 'My profile · User management', canActivate: [authGuard], loadComponent: () => import('./profile/profile.component').then(m => m.ProfileComponent) },
  { path: 'logs', title: 'Audit logs · User management', canActivate: [adminGuard], loadComponent: () => import('./activity/audit.component').then(m => m.AuditComponent) },
  { path: 'dashboard', title: 'Dashboard · User management', canActivate: [adminGuard], loadComponent: () => import('./activity/dashboard.component').then(m => m.DashboardComponent) },
  { path: '**', redirectTo: '' }
];
