import { useId, useState, type KeyboardEvent, type ReactNode } from 'react';
import { cn } from '@/lib/utils';

export interface Option<T extends string> {
  value: T;
  label: string;
}

/** Single choice as a segmented control (prototype `.seg` with `data-single`). */
export function Segmented<T extends string>({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: T;
  options: Option<T>[];
  onChange: (value: T) => void;
}) {
  return (
    <div className="seg" role="group" aria-label={label}>
      {options.map((o) => (
        <button key={o.value} type="button" aria-pressed={o.value === value} onClick={() => onChange(o.value)}>
          {o.label}
        </button>
      ))}
    </div>
  );
}

/**
 * Several choices as a segmented control (prototype `.seg` with `data-multi`). At least one stays selected, as the
 * prototype does; `onEmpty` explains why a click was ignored.
 */
export function MultiSegmented<T extends string>({
  label,
  values,
  options,
  onChange,
  onEmpty,
}: {
  label: string;
  values: T[];
  options: Option<T>[];
  onChange: (values: T[]) => void;
  onEmpty?: () => void;
}) {
  const toggle = (v: T) => {
    if (values.includes(v)) {
      if (values.length === 1) return onEmpty?.();
      onChange(values.filter((x) => x !== v));
    } else {
      onChange(options.map((o) => o.value).filter((x) => x === v || values.includes(x)));
    }
  };
  return (
    <div className="seg" role="group" aria-label={label}>
      {options.map((o) => (
        <button key={o.value} type="button" aria-pressed={values.includes(o.value)} onClick={() => toggle(o.value)}>
          {o.label}
        </button>
      ))}
    </div>
  );
}

/** On/off switch (prototype `.sw`): a real checkbox, so it works with the keyboard and screen readers. */
export function Switch({
  checked,
  onChange,
  label,
  disabled,
}: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label: string;
  disabled?: boolean;
}) {
  return (
    <label className="sw">
      <input
        type="checkbox"
        role="switch"
        aria-label={label}
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
      />
      <span />
    </label>
  );
}

/** A row with a title, a description and a control on the right (prototype `.setting`). */
export function Setting({
  title,
  description,
  children,
}: {
  title: string;
  description?: ReactNode;
  children: ReactNode;
}) {
  return (
    <div className="setting">
      <div>
        <div className="t">{title}</div>
        {description && <div className="d">{description}</div>}
      </div>
      {children}
    </div>
  );
}

/**
 * Tags typed into one box (prototype `.chipsin`): Enter or comma adds, × removes, Backspace on an empty box removes the
 * last tag. Duplicates (ignoring case) are not added.
 */
export function ChipsInput({
  label,
  values,
  onChange,
  max = 25,
  maxLength = 100,
  placeholder = 'Type and press Enter',
  error,
}: {
  label: string;
  values: string[];
  onChange: (values: string[]) => void;
  max?: number;
  maxLength?: number;
  placeholder?: string;
  error?: string;
}) {
  const id = useId();
  const [text, setText] = useState('');
  const add = () => {
    const v = text.trim().replace(/,$/, '').trim();
    if (!v) return;
    if (values.length < max && !values.some((x) => x.toLowerCase() === v.toLowerCase())) onChange([...values, v]);
    setText('');
  };
  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter' || e.key === ',') {
      e.preventDefault();
      add();
    } else if (e.key === 'Backspace' && !text && values.length) {
      onChange(values.slice(0, -1));
    }
  };
  return (
    <div className="field">
      <label className="lbl" htmlFor={id}>
        {label}
      </label>
      <div className={cn('chipsin', error && 'border-red')}>
        {values.map((v, i) => (
          <span key={v} className="tagx">
            {v}
            <button type="button" aria-label={`Remove ${v}`} onClick={() => onChange(values.filter((_, j) => j !== i))}>
              ×
            </button>
          </span>
        ))}
        <input
          id={id}
          value={text}
          maxLength={maxLength}
          placeholder={values.length >= max ? `Up to ${max}` : placeholder}
          disabled={values.length >= max}
          aria-invalid={error ? true : undefined}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={onKey}
          onBlur={add}
        />
      </div>
      {error && (
        <div className="err" role="alert">
          {error}
        </div>
      )}
    </div>
  );
}
