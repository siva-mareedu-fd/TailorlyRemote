import { Outlet } from 'react-router';
import { BrandMark } from '@/components/BrandMark';

/** Split layout from design/tailorly-auth-B.html: ink brand panel (hidden below 960 px) + form panel. */
export function AuthLayout() {
  return (
    <div className="grid min-h-full grid-cols-1 min-[961px]:grid-cols-[minmax(0,1.05fr)_minmax(0,0.95fr)]">
      <aside
        aria-label="About Tailorly Remote"
        className="relative hidden flex-col gap-7 bg-ink bg-[radial-gradient(var(--ink-line)_1px,transparent_1px)] bg-[size:22px_22px] px-14 py-10 text-ink-text min-[961px]:flex"
      >
        <BrandMark size={26} className="text-[17px]" />
        <div>
          <h1 className="mb-3.5 max-w-[19ch] text-[clamp(28px,3.3vw,40px)] leading-[1.12] font-semibold tracking-[-0.03em]">
            Your daily shortlist of <em className="not-italic text-signal">remote roles</em>, and the résumé to win
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
      </aside>

      <section className="flex flex-col bg-bg px-5 pt-5 pb-6 min-[961px]:px-10 min-[961px]:pt-7 min-[961px]:pb-8">
        <div className="flex min-h-10 items-center min-[961px]:hidden">
          <BrandMark tone="page" />
        </div>
        <div className="grid flex-1 place-items-center py-5">
          <div className="w-full max-w-[420px] rounded-card border border-border bg-surface p-[22px] shadow-card min-[961px]:p-7">
            <Outlet />
          </div>
        </div>
      </section>
    </div>
  );
}
