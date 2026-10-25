import { cva, type VariantProps } from 'class-variance-authority';
import { Slot } from 'radix-ui';
import type * as React from 'react';
import { cn } from '@/lib/utils';

// Mirrors `.btn` and its variants in design/tailorly-app.html (#system).
const buttonVariants = cva(
  'inline-flex items-center justify-center gap-1.5 whitespace-nowrap rounded-control border font-medium no-underline ' +
    'cursor-pointer transition-colors hover:no-underline disabled:pointer-events-none disabled:opacity-50 ' +
    'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent',
  {
    variants: {
      variant: {
        default: 'border-border bg-surface text-text hover:bg-surface-2',
        primary: 'border-accent bg-accent text-on-accent font-semibold hover:bg-accent-hover',
        ghost: 'border-transparent bg-transparent text-accent hover:bg-accent-soft',
        danger: 'border-[color-mix(in_srgb,var(--red)_40%,var(--border))] bg-transparent text-red hover:bg-red-soft',
        icon: 'size-[30px] border-ink-line bg-transparent p-0 text-ink-muted hover:text-ink-text',
        'icon-light': 'size-[30px] border-border bg-transparent p-0 text-muted hover:bg-surface-2 hover:text-text',
      },
      size: {
        default: 'px-[13px] py-2 text-sm',
        sm: 'px-2.5 py-[5px] text-[13px]',
        icon: '',
      },
    },
    defaultVariants: { variant: 'default', size: 'default' },
  },
);

export function Button({
  className,
  variant,
  size,
  asChild = false,
  ...props
}: React.ComponentProps<'button'> & VariantProps<typeof buttonVariants> & { asChild?: boolean }) {
  const Comp = asChild ? Slot.Root : 'button';
  return <Comp data-slot="button" className={cn(buttonVariants({ variant, size, className }))} {...props} />;
}

export { buttonVariants };
