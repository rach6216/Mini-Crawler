import { Route, Routes } from 'react-router-dom';
import { Layout } from './components/Layout';
import { HistoryPage } from './pages/HistoryPage';
import { JobDetailsPage } from './pages/JobDetailsPage';
import { NotFoundPage } from './pages/NotFoundPage';
import { StartCrawlPage } from './pages/StartCrawlPage';

export function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<StartCrawlPage />} />
        <Route path="jobs/:jobId" element={<JobDetailsPage />} />
        <Route path="history" element={<HistoryPage />} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}

