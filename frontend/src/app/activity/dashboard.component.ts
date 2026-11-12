/** Present six reconciled source summaries without invented windows or stale fallback values. */
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize } from 'rxjs';
import { DashboardView } from '../core/api.models';
import { ActivityApiService, activityError } from './activity-api.service';
/** Preserve the inherited restrained hierarchy with native lists and readable tabular numbers. */
@Component({ selector: 'app-dashboard', standalone: true, imports: [DatePipe],
  styles: [':host { display: block; } .counts { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: var(--space-6); margin-bottom: var(--space-12); } .count { border: 1px solid var(--border); border-radius: var(--radius); padding: var(--space-6); background: var(--surface); } .value { font-size: var(--text-2xl); font-variant-numeric: tabular-nums; } h2 { font-size: var(--text-xl); } section { margin-bottom: var(--space-12); } ul { padding-left: var(--space-6); } li { margin-bottom: var(--space-3); overflow-wrap: anywhere; } time { font-variant-numeric: tabular-nums; }'],
  template: `
    <h1>Dashboard</h1>
    <p class="muted">Account and approved audit summaries from one consistent source snapshot.</p>
    @if (loading()) { <p role="status" aria-live="polite">Loading dashboard…</p> }
    @if (error()) {
      <p class="alert-error" role="alert">{{ error() }}</p>
      <button class="button secondary" type="button" (click)="load()">Retry</button>
    }
    @if (data(); as summary) {
      @if (summary.total === 0) { <p role="status">No accounts are available.</p> }
      <div class="counts">
        <section class="count" aria-labelledby="total-heading"><h2 id="total-heading">Total accounts</h2><div class="value" data-testid="total">{{ summary.total }}</div></section>
        <section class="count" aria-labelledby="active-heading"><h2 id="active-heading">Active accounts</h2><div class="value" data-testid="active">{{ summary.active }}</div></section>
        <section class="count" aria-labelledby="inactive-heading"><h2 id="inactive-heading">Inactive accounts</h2><div class="value" data-testid="inactive">{{ summary.inactive }}</div></section>
      </div>
      <section aria-labelledby="logins-heading">
        <h2 id="logins-heading">Recent logins</h2><p class="muted">Latest 10 recorded login outcomes, newest first · UTC. No time window is applied.</p>
        @if (summary.recentLogins.length === 0) { <p>No login records are available.</p> }
        @else { <ul>
          @for (item of summary.recentLogins; track item.id) {
            <li [attr.data-audit-id]="item.id">{{ item.status }} @if (item.user_id) { <span>· Actor ID {{ item.user_id }}</span> }
              · <time [attr.datetime]="item.timestamp">{{ item.timestamp | date:'yyyy-MM-dd HH:mm:ss.SSS':'UTC' }}</time></li>
          }
        </ul> }
      </section>
      <section aria-labelledby="countries-heading">
        <h2 id="countries-heading">Country distribution</h2><p class="muted">Accounts with a populated country only; missing and empty values are excluded.</p>
        @if (summary.countryDistribution.length === 0) { <p>No populated countries are available.</p> }
        @else { <ul>@for (item of summary.countryDistribution; track item.country) { <li>{{ item.country }}: {{ item.count }}</li> }</ul> }
      </section>
      <section aria-labelledby="audit-heading">
        <h2 id="audit-heading">Audit summary</h2><p class="muted">All approved recorded actions, grouped by outcome.</p>
        @if (summary.auditSummary.length === 0) { <p>No audit activity is available.</p> }
        @else { <ul>@for (item of summary.auditSummary; track item.action) { <li>{{ item.action }}: {{ item.success }} success, {{ item.failure }} failure</li> }</ul> }
      </section>
    }
  ` })
export class DashboardComponent {
  private readonly destroyRef = inject(DestroyRef);
  readonly data = signal<DashboardView | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');
  /** Fetch only the approved read; no mutation occurs on dashboard entry. */
  constructor(private readonly api: ActivityApiService) { this.load(); }
  /** Clear previous values before every read and expose fixed safe failure guidance. */
  load(): void {
    if (this.loading()) return;
    this.data.set(null); this.error.set(''); this.loading.set(true);
    this.api.dashboard().pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.loading.set(false))).subscribe({
      next: summary => this.data.set(summary), error: (error: unknown) => this.error.set(activityError(error))
    });
  }
}
