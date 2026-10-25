import { Moon, Sun } from 'lucide-react';
import { Link, Outlet } from 'react-router';
import { BrandMark } from '@/components/BrandMark';
import { useTheme } from '@/lib/theme';

/** Split layout from design/tailorly-auth-B.html: ink brand panel (hidden below 960 px) + form panel. */
export function AuthLayout() {
  const { theme, toggle } = useTheme();
  return (
    <div className="grid min-h-full grid-cols-1 min-[961px]:grid-cols-[minmax(0,1.05fr)_minmax(0,0.95fr)]">
      <aside
        aria-label="About Tailorly Remote"
        className="relative hidden flex-col gap-7 bg-ink bg-[radial-gradient(var(--ink-line)_1px,transparent_1px)] bg-[size:22px_22px] px-14 py-10 text-ink-text min-[961px]:flex"
      >
        <BrandMark size={26} className="text-[17px]" />
        <div>
          <h1 className="mb-3.5 max-w-[19ch] text-[clamp(28px,3.3vw,40px)] leading-[1.12] font-semibold tracking-[-0.03em]">
            Your daily shortlist of <em className="text-signal not-italic">remote roles</em>, and the résumé to win
            them.
          </h1>
          <p className="max-w-[46ch] text-base text-ink-muted">
            We find remote jobs that are actually open to India, explain why each one fits, and tailor your résumé
            without breaking its formatting or inventing a single fact.
          </p>
        </div>
        <ul className="m-0 grid list-none gap-2.5 p-0 text-sm text-ink-muted">
          {[
            ['eligibility', 'See whether a “remote” job accepts candidates from India, with the evidence.'],
            ['explained', 'Every match comes with a score and the reasons behind it.'],
            ['diff', 'Review every résumé change line by line. You approve what ships.'],
          ].map(([k, text]) => (
            <li key={k} className="flex items-baseline gap-3">
              <span className="min-w-[78px] font-mono text-xs text-signal">{k}</span>
              <span>{text}</span>
            </li>
          ))}
        </ul>
        <ExampleCard />
        <div className="text-xs text-ink-muted">
          Illustrative example. Real scores depend on your résumé and the job.
        </div>
      </aside>

      <section className="flex flex-col bg-bg px-5 pt-5 pb-6 min-[961px]:px-10 min-[961px]:pt-7 min-[961px]:pb-8">
        <div className="flex min-h-10 items-center justify-between">
          <BrandMark tone="page" className="min-[961px]:invisible" />
          <button
            type="button"
            onClick={toggle}
            aria-label="Toggle light and dark theme"
            title={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
            className="grid size-9 cursor-pointer place-items-center rounded-control border border-border bg-transparent text-muted hover:bg-surface hover:text-text"
          >
            {theme === 'dark' ? <Sun size={18} strokeWidth={1.8} /> : <Moon size={18} strokeWidth={1.8} />}
          </button>
        </div>
        <div className="grid flex-1 place-items-center py-5">
          <section
            className="w-full max-w-[420px] rounded-card border border-border bg-surface p-[22px] shadow-card min-[961px]:p-7"
            aria-live="polite"
          >
            <Outlet />
          </section>
        </div>
        <footer className="flex justify-center gap-4 pt-2 text-[12.5px] text-muted">
          <Link to="/privacy">Privacy</Link>
          <Link to="/terms">Terms</Link>
          <span>© Tailorly Remote</span>
        </footer>
      </section>
    </div>
  );
}

/** The static digest-row-with-diff example of the brand panel. */
function ExampleCard() {
  return (
    <div
      className="max-w-[540px] overflow-hidden rounded-card border border-ink-line bg-ink-card"
      role="img"
      aria-label="Example of a daily digest row with a résumé diff"
    >
      <div className="flex justify-between border-b border-ink-line px-4 py-[9px] text-xs text-ink-muted">
        <span className="font-mono">shortlist · 18:00 IST</span>
        <span>Example</span>
      </div>
      <div className="grid gap-[14px] p-4">
        <div className="grid grid-cols-[34px_1fr] gap-3">
          <div className="font-mono text-2xl leading-[1.1] font-semibold text-signal">9</div>
          <div>
            <div className="text-[15.5px] font-semibold tracking-[-0.01em]">Senior Backend Engineer (.NET)</div>
            <div className="text-[13px] text-ink-muted">Acme Cloud · Remote, APAC</div>
            <div className="mt-[9px] flex flex-wrap gap-1.5 font-mono text-[11px]">
              <span className="rounded-chip bg-signal-soft px-[7px] py-px text-signal">india_eligible</span>
              <span className="rounded-chip border border-ink-line px-[7px] py-px text-ink-muted">$60k–$90k</span>
              <span className="rounded-chip border border-ink-line px-[7px] py-px text-ink-muted">
                Source: Himalayas ↗
              </span>
            </div>
          </div>
        </div>
        <div className="overflow-hidden rounded-control border border-ink-line font-mono text-xs leading-[1.6] text-ink-text">
          <div className="bg-[rgba(240,138,130,.14)] px-3 py-[3px]">
            <span className="mr-2 text-ink-muted select-none">−</span>Built REST APIs for the payments team.
          </div>
          <div className="bg-[rgba(61,220,151,.14)] px-3 py-[3px]">
            <span className="mr-2 text-ink-muted select-none">+</span>Built REST APIs in .NET and PostgreSQL for the
            payments team.
          </div>
        </div>
        <div>
          <div className="flex justify-between text-[12.5px] text-ink-muted">
            <span>ATS match (estimated)</span>
            <span className="font-mono">
              58 → <b className="font-semibold text-signal">81</b>
            </span>
          </div>
          <div className="mt-1.5 h-[5px] overflow-hidden rounded-[9px] bg-ink-line">
            <i className="block h-full w-[81%] rounded-[9px] bg-signal" />
          </div>
        </div>
      </div>
    </div>
  );
}
