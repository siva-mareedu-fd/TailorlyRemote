import { createBrowserRouter, Navigate } from 'react-router';
import { AuthPlaceholder, PlaceholderPage } from '@/pages/Placeholder';
import { NotFoundPage } from '@/pages/NotFound';
import { AppShell } from './AppShell';
import { AuthLayout } from './AuthLayout';

// One empty page per screen in design/tailorly-app.html, labelled with the milestone that builds it.
export const router = createBrowserRouter([
  {
    element: <AppShell />,
    children: [
      { index: true, element: <Navigate to="/shortlist" replace /> },
      {
        path: 'shortlist',
        element: (
          <PlaceholderPage
            title="Today’s shortlist"
            sub="Your daily digest of remote roles open to India."
            milestone="M3"
          />
        ),
      },
      {
        path: 'jobs/:id',
        element: (
          <PlaceholderPage
            title="Job detail and tailoring"
            sub="Fit score, ATS analysis, tailored résumé and cover letter."
            milestone="M4"
          />
        ),
      },
      {
        path: 'tracker',
        element: (
          <PlaceholderPage title="Tracker" sub="Saved → Tailored → Applied → Interviewing → Offer." milestone="M5" />
        ),
      },
      {
        path: 'add-job',
        element: (
          <PlaceholderPage
            title="Add a job"
            sub="Paste a job from LinkedIn, Naukri or anywhere else."
            milestone="M5"
            narrow
          />
        ),
      },
      {
        path: 'resume',
        element: (
          <PlaceholderPage title="Résumé" sub="Your .docx, its parsed profile and versions." milestone="M1" narrow />
        ),
      },
      {
        path: 'preferences',
        element: (
          <PlaceholderPage
            title="Preferences"
            sub="Search scope, roles, salary and digest schedule."
            milestone="M2"
            narrow
          />
        ),
      },
      {
        path: 'usage',
        element: (
          <PlaceholderPage title="Usage" sub="Discovery and Documents budgets this month." milestone="M5" narrow />
        ),
      },
      {
        path: 'account',
        element: (
          <PlaceholderPage
            title="Account"
            sub="Profile, password, consent and account deletion."
            milestone="M1"
            narrow
          />
        ),
      },
      {
        path: 'admin',
        element: (
          <PlaceholderPage title="Admin console" sub="Sources, companies, AI config, usage and flags." milestone="M6" />
        ),
      },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
  {
    path: 'onboarding',
    element: (
      <div className="h-full overflow-auto">
        <PlaceholderPage
          title="Welcome to Tailorly"
          sub="Consent → upload → review profile → preferences → schedule."
          milestone="M1"
          narrow
        />
      </div>
    ),
  },
  {
    element: <AuthLayout />,
    children: [
      { path: 'signin', element: <AuthPlaceholder title="Welcome back" lead="Sign in to see today’s shortlist." /> },
      {
        path: 'signup',
        element: <AuthPlaceholder title="Create your account" lead="Remote roles open to India, scored for you." />,
      },
    ],
  },
]);
