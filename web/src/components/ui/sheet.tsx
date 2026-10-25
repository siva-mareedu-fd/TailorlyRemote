import { Dialog as SheetPrimitive } from 'radix-ui';
import type * as React from 'react';
import { cn } from '@/lib/utils';

// shadcn/ui Sheet (Radix Dialog), styled to the tokens. Used for the mobile navigation drawer.
export const Sheet = SheetPrimitive.Root;
export const SheetTrigger = SheetPrimitive.Trigger;
export const SheetClose = SheetPrimitive.Close;
export const SheetTitle = SheetPrimitive.Title;
export const SheetDescription = SheetPrimitive.Description;

export function SheetContent({
  className,
  children,
  side = 'left',
  ...props
}: React.ComponentProps<typeof SheetPrimitive.Content> & { side?: 'left' | 'right' }) {
  return (
    <SheetPrimitive.Portal>
      <SheetPrimitive.Overlay className="fixed inset-0 z-[69] bg-[rgba(10,11,9,.55)] data-[state=open]:animate-in data-[state=open]:fade-in-0 data-[state=closed]:animate-out data-[state=closed]:fade-out-0" />
      <SheetPrimitive.Content
        className={cn(
          'fixed inset-y-0 z-70 flex flex-col shadow-card outline-none',
          'data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=open]:duration-200 data-[state=closed]:duration-200',
          side === 'left'
            ? 'left-0 data-[state=open]:slide-in-from-left data-[state=closed]:slide-out-to-left'
            : 'right-0 data-[state=open]:slide-in-from-right data-[state=closed]:slide-out-to-right',
          className,
        )}
        {...props}
      >
        {children}
      </SheetPrimitive.Content>
    </SheetPrimitive.Portal>
  );
}
