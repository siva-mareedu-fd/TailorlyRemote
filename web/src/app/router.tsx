import { createBrowserRouter, type RouteObject } from 'react-router';
import { AccountPage } from '@/pages/AccountPage';
import { AdminPage } from '@/pages/AdminPage';
import { AuthPage } from '@/pages/auth/AuthPage';
import { ConfirmDeletionPage, ResetPasswordPage, VerifyEmailPage } from '@/pages/auth/EmailLinkPages';
import { PrivacyPage, TermsPage } from '@/pages/auth/LegalPages';
import { ErrorPage } from '@/pages/ErrorPage';
import { NotFoundPage } from '@/pages/NotFound';
import { OnboardingPage } from '@/pages/OnboardingPage';
import { PlaceholderPage } from '@/pages/Placeholder';
import { PreferencesPage } from '@/pages/PreferencesPage';
import { ResumePage } from '@/pages/ResumePage';
import { UsagePage } from '@/pages/UsagePage';
import { AppShell } from './AppShell';
import { AuthLayout } from './AuthLayout';
import { HomeRedirect, RequireAuth } from './RequireAuth';

// Screens from design/tailorly-app.html; those not built yet carry the milestone that builds them.
const routes: RouteObject[] = [
  {
    element: <AppShell />,
    children: [
      { index: true, element: <HomeRedirect /> },
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
      { path: 'resume', element: <ResumePage /> },
      { path: 'preferences', element: <PreferencesPage /> },
      { path: 'usage', element: <UsagePage /> },
      { path: 'account', element: <AccountPage /> },
      { path: 'admin', element: <AdminPage /> },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
  {
    path: 'onboarding',
    element: (
      <RequireAuth>
        <OnboardingPage />
      </RequireAuth>
    ),
  },
  {
    element: <AuthLayout />,
    children: [
      { path: 'signin', element: <AuthPage mode="signin" /> },
      { path: 'signup', element: <AuthPage mode="signup" /> },
      { path: 'verify-email', element: <VerifyEmailPage /> },
      { path: 'reset-password', element: <ResetPasswordPage /> },
      { path: 'confirm-deletion', element: <ConfirmDeletionPage /> },
      { path: 'privacy', element: <PrivacyPage /> },
      { path: 'terms', element: <TermsPage /> },
    ],
  },
];

export const router = createBrowserRouter([{ errorElement: <ErrorPage />, children: routes }]);
