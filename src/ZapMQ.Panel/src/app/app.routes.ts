import { Routes } from '@angular/router';
import { requireSession } from './core/auth';

export const routes: Routes = [
  { path: 'login', title: 'Entrar · ZapMQ', loadComponent: () => import('./pages/login').then((m) => m.LoginPage) },
  {
    path: '',
    canActivate: [requireSession],
    loadComponent: () => import('./layout/shell').then((m) => m.Shell),
    children: [
      { path: '', pathMatch: 'full', title: 'Visão geral · ZapMQ', loadComponent: () => import('./pages/overview').then((m) => m.OverviewPage) },
      { path: 'filas', title: 'Filas · ZapMQ', loadComponent: () => import('./pages/queues').then((m) => m.QueuesPage) },
      { path: 'filas/:name', title: 'Fila · ZapMQ', loadComponent: () => import('./pages/queue-detail').then((m) => m.QueueDetailPage) },
      { path: 'mortas', title: 'Mensagens mortas · ZapMQ', loadComponent: () => import('./pages/dead-letters').then((m) => m.DeadLettersPage) },
      { path: 'aplicacoes', title: 'Aplicações · ZapMQ', loadComponent: () => import('./pages/connections').then((m) => m.ConnectionsPage) },
    ],
  },
  { path: '**', redirectTo: '' },
];
