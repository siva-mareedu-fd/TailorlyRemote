import { Moon, Sun } from 'lucide-react';
import { NavLink } from 'react-router';
import { BrandMark } from '@/components/BrandMark';
import { Button } from '@/components/ui/button';
import { useMeta } from '@/api/queries';
import { useTheme } from '@/lib/theme';
import { cn } from '@/lib/utils';
import { adminNav, mainNav, type NavItem } from './routes';

/** Percent used per budget bucket; null until the usage API exists (M1). */
export interface BudgetSnapshot {
  discovery: number | null;
  documents: number | null;
}

function NavItemLink({ item, onNavigate }: { item: NavItem; onNavigate?: () => void }) {
  return (
    <NavLink
      to={item.to}
      onClick={onNavigate}
      className={({ isActive }) =>
        cn(
          'flex items-center justify-between rounded-nav px-2.5 py-[7px] font-medium text-ink-muted no-underline',
          'hover:bg-ink-active hover:text-ink-text hover:no-underline',
          isActive && 'bg-ink-active text-ink-text shadow-[inset_2px_0_0_var(--signal)]',
        )
      }
    >
      <span>{item.label}</span>
    </NavLink>
  );
}

function BudgetBar({ label, percent }: { label: string; percent: number | null }) {
  const warn = percent !== null && percent >= 80;
  return (
    <div>
      <div className="mb-1 flex justify-between">
        <span>{label}</span>
        <span className="font-mono">{percent === null ? '—' : `${percent}%`}</span>
      </div>
      <div
        className="h-1 overflow-hidden rounded-[9px] bg-ink-line"
        role="progressbar"
        aria-label={`${label} budget used`}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={percent ?? undefined}
      >
        <b className={cn('block h-full', warn ? 'bg-[#f0b44c]' : 'bg-signal')} style={{ width: `${percent ?? 0}%` }} />
      </div>
    </div>
  );
}

export function Sidebar({
  budget,
  showAdmin,
  onNavigate,
}: {
  budget: BudgetSnapshot;
  showAdmin: boolean;
  onNavigate?: () => void;
}) {
  const { theme, toggle } = useTheme();
  const meta = useMeta();

  return (
    <div className="flex h-full flex-col gap-[14px] overflow-auto bg-ink px-3 py-[18px] text-ink-text">
      <div className="px-2">
        <BrandMark />
        <div className="mt-1 font-mono text-[10.5px] text-ink-muted" data-testid="api-version">
          {meta.data ? `api v${meta.data.version}` : meta.isError ? 'api offline' : ' '}
        </div>
      </div>

      <nav aria-label="Main" className="grid gap-0.5">
        {mainNav.map((item) => (
          <NavItemLink key={item.to} item={item} onNavigate={onNavigate} />
        ))}
        {showAdmin && (
          <>
            <div className="px-2.5 pt-2 pb-0.5 font-mono text-[10.5px] uppercase tracking-[0.08em] text-ink-muted opacity-80">
              Admin
            </div>
            {adminNav.map((item) => (
              <NavItemLink key={item.to} item={item} onNavigate={onNavigate} />
            ))}
          </>
        )}
      </nav>

      <div
        className="mt-auto grid gap-2.5 border-t border-ink-line px-2 pt-[14px] text-xs text-ink-muted"
        aria-label="AI budget this month"
      >
        <BudgetBar label="Discovery" percent={budget.discovery} />
        <BudgetBar label="Documents" percent={budget.documents} />
      </div>

      <div className="flex items-center gap-[9px] p-2">
        <div className="grid size-7 flex-none place-items-center rounded-avatar bg-[#23221b] font-mono text-xs font-semibold text-signal">
          ··
        </div>
        <div className="min-w-0 flex-1 text-[13px] leading-tight text-ink-text">
          Not signed in
          <small className="block text-[11px] text-ink-muted">Asia/Kolkata</small>
        </div>
        <Button
          variant="icon"
          size="icon"
          type="button"
          onClick={toggle}
          aria-label="Toggle light and dark theme"
          title={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
        >
          {theme === 'dark' ? <Sun size={16} strokeWidth={1.8} /> : <Moon size={16} strokeWidth={1.8} />}
        </Button>
      </div>
    </div>
  );
}
