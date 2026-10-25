import { createContext, useContext } from 'react';
import type { Me } from '@/api/queries';

/**
 * The signed-in user as `RequireAuth` last rendered it. Pages under `RequireAuth` read it from here, not from
 * `useMe()`, so a sign-out (which sets `me` to null) unmounts them instead of rendering them with no user.
 */
export const SignedInContext = createContext<Me | null>(null);

export function useSignedInMe(): Me {
  const me = useContext(SignedInContext);
  if (!me) throw new Error('useSignedInMe must be used inside <RequireAuth>');
  return me;
}
