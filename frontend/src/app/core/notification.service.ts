/** Share accessible feedback and clear it across identity changes. */
import { Injectable, signal } from '@angular/core';
@Injectable({ providedIn: 'root' })
export class NotificationService {
  readonly message = signal('');
  /** Announce a safe outcome. */
  show(message: string): void { this.message.set(message); }
  /** Remove feedback belonging to the previous account. */
  clear(): void { this.message.set(''); }
}
