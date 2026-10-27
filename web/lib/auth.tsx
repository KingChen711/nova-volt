"use client";

import { User, UserManager, WebStorageStateStore } from "oidc-client-ts";
import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";

/** Người dùng đã đăng nhập: tên, realm role lấy từ access token, và chính token để gọi MOM. */
export type Session = {
  user: string;
  roles: string[];
  accessToken: string;
};

type AuthState = {
  session: Session | null;
  loading: boolean;
  signIn: () => Promise<void>;
  signOut: () => Promise<void>;
  completeSignIn: () => Promise<void>;
};

const AuthContext = createContext<AuthState | null>(null);

let manager: UserManager | null = null;

function userManager(): UserManager {
  // Chỉ tạo ở trình duyệt: oidc-client-ts cần window và sessionStorage.
  manager ??= new UserManager({
    authority: process.env.NEXT_PUBLIC_KEYCLOAK_URL!,
    client_id: process.env.NEXT_PUBLIC_KEYCLOAK_CLIENT!,
    redirect_uri: `${window.location.origin}/callback`,
    post_logout_redirect_uri: window.location.origin,
    response_type: "code",
    scope: "openid profile",
    // Token sống 10 phút như hệ thật; web tự xin token mới để người dùng không bị đá ra giữa ca.
    automaticSilentRenew: true,
    userStore: new WebStorageStateStore({ store: window.sessionStorage }),
  });
  return manager;
}

function toSession(user: User | null): Session | null {
  if (!user || user.expired) {
    return null;
  }

  const payload = JSON.parse(atob(user.access_token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/"))) as {
    preferred_username?: string;
    realm_access?: { roles?: string[] };
  };
  return {
    user: payload.preferred_username ?? user.profile.sub,
    roles: payload.realm_access?.roles ?? [],
    accessToken: user.access_token,
  };
}

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [session, setSession] = useState<Session | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const um = userManager();
    const update = (user: User | null) => setSession(toSession(user));
    um.getUser().then(update).finally(() => setLoading(false));
    um.events.addUserLoaded(update);
    um.events.addUserUnloaded(() => setSession(null));
    return () => {
      um.events.removeUserLoaded(update);
    };
  }, []);

  const signIn = useCallback(() => userManager().signinRedirect(), []);
  const signOut = useCallback(() => userManager().signoutRedirect(), []);
  const completeSignIn = useCallback(async () => {
    setSession(toSession(await userManager().signinRedirectCallback()));
  }, []);

  const value = useMemo(() => ({ session, loading, signIn, signOut, completeSignIn }), [session, loading, signIn, signOut, completeSignIn]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error("useAuth must be used inside AuthProvider");
  }

  return context;
}
