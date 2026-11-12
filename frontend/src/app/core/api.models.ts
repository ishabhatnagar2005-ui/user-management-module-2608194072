/** Define safe HTTP contracts independently of password-bearing persistence documents. */
export interface LoginRequest { username: string; password: string; }
export interface LoginResult { accessToken: string; tokenType: 'Bearer'; expiresAt: string; }
export interface ApiError { type: string; title: string; status: number; errors: Record<string, string[]>; correlationId: string; }
export interface UserView { id: string; username: string; address?: string | null; city?: string | null; country?: string | null; education?: string | null; is_active: boolean; created_at: string; updated_at: string; }
export type Role = 'Admin' | 'User';

/** Carry only administrator-create fields; policy and lifecycle values are server-owned. */
export interface CreateUserRequest {
  username: string; password: string; address?: string | null; city?: string | null;
  country?: string | null; education?: string | null; aadhaar_no?: string | null; pan_no?: string | null;
}

/** Preserve the approved directory URL/HTTP state without total-count assumptions. */
export interface DirectoryQuery {
  limit: number; skip: number; search: string; country: string; education: string;
  status: '' | 'active' | 'inactive'; sortBy: 'username' | 'city' | 'country' | 'education'; sortDirection: 'asc' | 'desc';
}
/** Return only safe records and the requested offset window. */
export interface DirectoryPage { items: UserView[]; limit: number; skip: number; }

/** Permit only directory patch fields; absent identifiers remain unchanged. */
export type UpdateUserRequest = Partial<Omit<CreateUserRequest, 'password'>>;

/** Permit only own-profile details; target identity and credentials cannot be submitted here. */
export type ProfileUpdateRequest = Pick<UserView, 'address' | 'city' | 'country' | 'education'>;
/** Carry secrets only in the explicit password change request. */
export interface PasswordChangeRequest { currentPassword: string; newPassword: string; }

/** Restrict audit display to approved source outcomes with no personal content. */
export type AuditAction = 'login' | 'user.create' | 'user.read' | 'user.list' | 'user.update' | 'user.deactivate' | 'profile.update' | 'password.change';
/** Omit unavailable actor identity; never substitute a username or invented identity. */
export interface AuditView { id: string; user_id?: string; action: AuditAction; status: 'success' | 'failure'; timestamp: string; }
/** Carry the fixed bounded read without query controls. */
export interface AuditPage { items: AuditView[]; }
/** Count populated source country labels only. */
export interface CountryCount { country: string; count: number; }
/** Group only recorded approved action outcomes. */
export interface AuditActivity { action: AuditAction; success: number; failure: number; }
/** Represent the six source-backed dashboard summaries. */
export interface DashboardView { total: number; active: number; inactive: number; recentLogins: AuditView[]; countryDistribution: CountryCount[]; auditSummary: AuditActivity[]; }
