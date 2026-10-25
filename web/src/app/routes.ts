/** Main navigation, in the order of the prototype sidebar (design/tailorly-app.html). */
export interface NavItem {
  to: string;
  label: string;
  admin?: boolean;
}

export const mainNav: NavItem[] = [
  { to: '/shortlist', label: 'Shortlist' },
  { to: '/tracker', label: 'Tracker' },
  { to: '/add-job', label: 'Add a job' },
  { to: '/resume', label: 'Résumé' },
  { to: '/preferences', label: 'Preferences' },
  { to: '/usage', label: 'Usage' },
  { to: '/account', label: 'Account' },
];

export const adminNav: NavItem[] = [{ to: '/admin', label: 'Admin console', admin: true }];
