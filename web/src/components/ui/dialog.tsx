import { Dialog as DialogPrimitive } from 'radix-ui';
import type * as React from 'react';
import { cn } from '@/lib/utils';

// shadcn/ui Dialog (Radix): focus trap, Esc to close, labelled title. Styled as `.dialog` in the prototype.
export const Dialog = DialogPrimitive.Root;
export const DialogTrigger = DialogPrimitive.Trigger;
export const DialogClose = DialogPrimitive.Close;

export function DialogContent({ className, children, ...props }: React.ComponentProps<typeof DialogPrimitive.Content>) {
  return (
    <DialogPrimitive.Portal>
      <DialogPrimitive.Overlay className="fixed inset-0 z-80 bg-[rgba(10,11,9,.55)] data-[state=open]:animate-in data-[state=open]:fade-in-0" />
      <DialogPrimitive.Content
        className={cn(
          'fixed top-1/2 left-1/2 z-81 w-[calc(100%-40px)] max-w-[440px] -translate-x-1/2 -translate-y-1/2',
          'rounded-dialog border border-border bg-surface p-[22px] shadow-card outline-none',
          'data-[state=open]:animate-in data-[state=open]:fade-in-0 data-[state=open]:zoom-in-95',
          className,
        )}
        {...props}
      >
        {children}
      </DialogPrimitive.Content>
    </DialogPrimitive.Portal>
  );
}

export function DialogTitle({ className, ...props }: React.ComponentProps<typeof DialogPrimitive.Title>) {
  return <DialogPrimitive.Title className={cn('mb-1.5 text-base font-semibold', className)} {...props} />;
}

export function DialogDescription({ className, ...props }: React.ComponentProps<typeof DialogPrimitive.Description>) {
  return <DialogPrimitive.Description className={cn('sub', className)} {...props} />;
}
