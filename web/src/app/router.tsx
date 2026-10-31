import { createBrowserRouter, type RouteObject } from 'react-router';
import { ShortlistPage } from '@/features/shortlist/ShortlistPage';
import { TailorPage } from '@/features/tailoring/TailorPage';
import { AccountPage } from '@/pages/AccountPage';
import { AdminPage } from '@/pages/AdminPage';
import { AuthPage } from '@/pages/auth/AuthPage';
import {
  ConfirmDeletionPage,
  ResetPasswordPage,
  UnsubscribePage,
  VerifyEmailPage,
  VerifyNotificationEmailPage,
} from '@/pages/auth/EmailLinkPages';
import { PrivacyPage, TermsPage } from '@/pages/auth/LegalPages';
import { DigestDetailPage, DigestsPage } from '@/pages/DigestsPage';
import { ErrorPage } from '@/pages/ErrorPage';
import { JobRedirect } from '@/pages/JobRedirect';
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
      { path: 'shortlist', element: <ShortlistPage /> },
      { path: 'digests', element: <DigestsPage /> },
      { path: 'digests/:id', element: <DigestDetailPage /> },
      { path: 'jobs/:id', element: <JobRedirect /> },
      { path: 'tailor/:userJobId', element: <TailorPage /> },
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
      { path: 'unsubscribe', element: <UnsubscribePage /> },
      { path: 'verify-notification-email', element: <VerifyNotificationEmailPage /> },
      { path: 'privacy', element: <PrivacyPage /> },
      { path: 'terms', element: <TermsPage /> },
    ],
  },
];

export const router = createBrowserRouter([{ errorElement: <ErrorPage />, children: routes }]);
