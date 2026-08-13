import { api } from './api';

export async function getCustomers(query = '') {
  const url = query ? `/api/sales/customers?query=${encodeURIComponent(query)}` : '/api/sales/customers';
  const data = await api.get(url);
  if (data && Array.isArray(data.items)) {
    return data.items;
  }
  if (Array.isArray(data)) {
    return data;
  }
  return [];
}

export async function createCustomer(customerData) {
  return await api.post('/api/sales/customers', customerData);
}

export async function getDefaultCustomer() {
  return await api.get('/api/sales/customers/default');
}
