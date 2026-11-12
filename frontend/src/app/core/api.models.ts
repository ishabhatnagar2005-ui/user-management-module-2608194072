/** Define safe HTTP contracts independently of password-bearing persistence documents. */
export interface LoginRequest { username: string; password: string; }
export interface LoginResult { accessToken: string; tokenType: 'Bearer'; expiresAt: string; }
export interface ApiError { type: string; title: string; status: number; errors: Record<string, string[]>; correlationId: string; }
export interface UserView { id: string; username: string; address?: string | null; city?: string | null; country?: string | null; education?: string | null; is_active: boolean; created_at: string; updated_at: string; }
export type Role = 'Admin' | 'User';
