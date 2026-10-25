import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';

const ToastContext = createContext<(message: string) => void>(() => undefined);

/** One polite toast at a time, as in the prototype (`say(...)`). */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [message, setMessage] = useState<string | null>(null);
  const timer = useRef<number | undefined>(undefined);

  const say = useCallback((m: string) => {
    setMessage(m);
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setMessage(null), 2600);
  }, []);

  useEffect(() => () => window.clearTimeout(timer.current), []);

  return (
    <ToastContext.Provider value={say}>
      {children}
      <div role="status" aria-live="polite" className="sr-only">
        {message}
      </div>
      {message && (
        <div className="toast" aria-hidden="true">
          {message}
        </div>
      )}
    </ToastContext.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components
export const useToast = () => useContext(ToastContext);
