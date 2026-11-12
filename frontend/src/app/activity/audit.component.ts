/** Present exact approved audit records in an accessible read-only table. */
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize } from 'rxjs';
import { AuditView } from '../core/api.models';
import { ActivityApiService, activityError } from './activity-api.service';
/** Preserve inherited typography, flat surfaces and native table semantics. */
@Component({ selector: 'app-audit', standalone: true, imports: [DatePipe],
  styles: [':host { display: block; } .table-scroll { overflow-x: auto; } table { width: 100%; border-collapse: collapse; } th, td { text-align: left; padding: var(--space-3); border-bottom: 1px solid var(--border); overflow-wrap: anywhere; } th { font-weight: 600; } time, .identity { font-variant-numeric: tabular-nums; }'],
  template: `
    <h1>Audit logs</h1>
    <p class="muted">Latest 100 approved records, newest first. Personal content is not included.</p>
    @if (loading()) { <p role="status" aria-live="polite">Loading audit logs…</p> }
    @if (error()) {
      <p class="alert-error" role="alert">{{ error() }}</p>
      <button class="button secondary" type="button" (click)="load()">Retry</button>
    }
    @if (!loading() && !error()) {
      @if (items().length === 0) { <p role="status">No audit records are available.</p> }
      @else {
        <div class="table-scroll" role="region" aria-label="Audit records" tabindex="0">
          <table>
            <caption class="muted">Approved audit outcomes · timestamps in UTC</caption>
            <thead><tr><th scope="col">ID</th><th scope="col">Actor ID</th><th scope="col">Action</th><th scope="col">Status</th><th scope="col">Timestamp (UTC)</th></tr></thead>
            <tbody>
              @for (item of items(); track item.id) {
                <tr><td class="identity">{{ item.id }}</td><td class="identity">{{ item.user_id }}</td><td>{{ item.action }}</td><td>{{ item.status }}</td>
                  <td><time [attr.datetime]="item.timestamp">{{ item.timestamp | date:'yyyy-MM-dd HH:mm:ss.SSS':'UTC' }}</time></td></tr>
              }
            </tbody>
          </table>
        </div>
      }
    }
  ` })
export class AuditComponent {
  private readonly destroyRef = inject(DestroyRef);
  readonly items = signal<AuditView[]>([]);
  readonly loading = signal(false);
  readonly error = signal('');
  /** Load safe records once; no render-time writes or extra audit controls exist. */
  constructor(private readonly api: ActivityApiService) { this.load(); }
  /** Retry failures without retaining stale rows or overlapping reads. */
  load(): void {
    if (this.loading()) return;
    this.items.set([]); this.error.set(''); this.loading.set(true);
    this.api.logs().pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.loading.set(false))).subscribe({
      next: page => this.items.set(page.items), error: (error: unknown) => this.error.set(activityError(error))
    });
  }
}
