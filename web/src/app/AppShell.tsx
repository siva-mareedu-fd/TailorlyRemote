import { Menu } from 'lucide-react';
import { useState } from 'react';
import { Outlet } from 'react-router';
import { Button } from '@/components/ui/button';
import { Sheet, SheetContent, SheetDescription, SheetTitle, SheetTrigger } from '@/components/ui/sheet';
import { Sidebar, type BudgetSnapshot } from './Sidebar';

// Real values arrive with GET /usage and GET /me (M1).
const NO_BUDGET: BudgetSnapshot = { discovery: null, documents: null };

/**
 * App shell from design/tailorly-app.html: 224 px ink sidebar on desktop; below 861 px a top bar with a
 * menu button that opens the same sidebar as a slide-in drawer (Radix dialog: focus trap, Esc to close).
 */
export function AppShell() {
  const [menuOpen, setMenuOpen] = useState(false);
  const showAdmin = true; // gated on the Admin role once /me exists (M1)

  return (
    <div className="grid h-full grid-rows-[auto_minmax(0,1fr)] shell:grid-cols-[var(--spacing-sidebar)_minmax(0,1fr)] shell:grid-rows-1">
      <aside aria-label="Sidebar" className="hidden shell:block">
        <Sidebar budget={NO_BUDGET} showAdmin={showAdmin} />
      </aside>

      <Sheet open={menuOpen} onOpenChange={setMenuOpen}>
        <header className="flex items-center gap-2.5 bg-ink px-[14px] py-2.5 text-ink-text shell:hidden">
          <SheetTrigger asChild>
            <Button variant="icon" size="icon" aria-label="Open menu">
              <Menu size={16} strokeWidth={2} />
            </Button>
          </SheetTrigger>
          <strong>Tailorly</strong> <span className="chip ok">remote</span>
        </header>
        <SheetContent side="left" className="w-drawer pt-[env(safe-area-inset-top,0px)]">
          <SheetTitle className="sr-only">Menu</SheetTitle>
          <SheetDescription className="sr-only">Main navigation</SheetDescription>
          <Sidebar budget={NO_BUDGET} showAdmin={showAdmin} onNavigate={() => setMenuOpen(false)} />
        </SheetContent>
      </Sheet>

      <main id="main" className="flex min-h-0 flex-col overflow-auto">
        <Outlet />
      </main>
    </div>
  );
}
