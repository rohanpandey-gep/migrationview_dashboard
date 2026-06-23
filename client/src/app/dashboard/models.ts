export interface ModuleCounts {
  module: string;
  published: number;
  missedQueue: number;
}

export interface BpcCard {
  bpcCode: number;
  bpcName: string;
  modules: ModuleCounts[];
}

export interface PrioritySection {
  displayName: string;
  cards: BpcCard[];
}

export interface DashboardResponse {
  queueCount: number;
  sections: PrioritySection[];
}

export interface PriorityGroup {
  displayName: string;
  bpcs: number[];
}

export interface SeedResponse {
  priorityGroups: PriorityGroup[];
  modules: string[];
  environments: string[];
}
